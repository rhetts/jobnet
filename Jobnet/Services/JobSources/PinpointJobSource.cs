using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Services.ApiUsage;
using Jobnet.Services.RateLimit;

namespace Jobnet.Services.JobSources;

/// <summary>
/// Pinpoint ATS (pinpointhq.com). Every customer board exposes its postings as JSON at
/// <c>https://{slug}.pinpointhq.com/postings.json</c> with no auth.
///
/// Payload shape (verified against article.pinpointhq.com):
/// <code>
/// { "data": [
///   { "id": "425252", "title": "...", "url": "https://{slug}.pinpointhq.com/en/postings/{uuid}",
///     "employment_type": "permanent_full_time" | "contract" | "...",
///     "workplace_type":  "onsite" | "hybrid" | "remote",
///     "compensation_minimum": 90000, "compensation_maximum": 130000,
///     "compensation_currency": "CAD", "compensation_frequency": "year" | "month" | "hour",
///     "compensation_visible": true,
///     "description": "&lt;div&gt;...&lt;/div&gt;",
///     "location": { "name": "Vancouver", "city": "Vancouver", "province": "British Columbia" },
///     "job": { "department": { "name": "Engineering" } } } ] }
/// </code>
/// Slug is the subdomain (e.g. <c>article</c> for article.pinpointhq.com).
/// </summary>
public sealed class PinpointJobSource : IJobSource
{
    public const string Provider = "ats_pinpoint";
    public string AtsType => "pinpoint";

    private readonly HttpClient _http;
    private readonly IApiUsageTracker _usage;
    private readonly IRateLimiter _rateLimiter;

    public PinpointJobSource(HttpClient http, IApiUsageTracker usage, IRateLimiter rateLimiter)
    {
        _http = http;
        _usage = usage;
        _rateLimiter = rateLimiter;
    }

    public async Task<IReadOnlyList<RawJobPosting>> FetchAsync(string slug, CancellationToken ct = default)
    {
        var url = $"https://{Uri.EscapeDataString(slug)}.pinpointhq.com/postings.json";

        await _rateLimiter.WaitAsync(Provider, ct);
        _usage.RecordCall(Provider);

        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Pinpoint HTTP {(int)resp.StatusCode} for slug '{slug}'");

        var payload = await resp.Content.ReadFromJsonAsync<Response>(cancellationToken: ct);
        var items = payload?.Data ?? new();
        var results = new List<RawJobPosting>(items.Count);
        foreach (var j in items)
        {
            if (string.IsNullOrEmpty(j.Id) || string.IsNullOrEmpty(j.Title)) continue;

            var (sMin, sMax, sCur, sPer) = ExtractSalary(j);
            results.Add(new RawJobPosting
            {
                NativeId = j.Id!,
                Title = j.Title!,
                Url = j.Url,
                Location = FormatLocation(j.Location),
                RemoteType = NormalizeRemote(j.WorkplaceType),
                EmploymentType = NormalizeEmployment(j.EmploymentType),
                Department = j.Job?.Department?.Name,
                DescriptionSnippet = SnippetCleaner.Clean(j.Description, maxChars: 500),
                SalaryMin = sMin,
                SalaryMax = sMax,
                SalaryCurrency = sCur,
                SalaryPeriod = sPer,
            });
        }
        return results;
    }

    private static string? FormatLocation(LocationObj? loc)
    {
        if (loc is null) return null;
        // Prefer the curated `name` field — most boards set it to a clean "City, Province"
        // string. Fall back to assembling from the city/province pair when name is blank.
        if (!string.IsNullOrWhiteSpace(loc.Name)) return loc.Name!.Trim();
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(loc.City))     parts.Add(loc.City!.Trim());
        if (!string.IsNullOrWhiteSpace(loc.Province)) parts.Add(loc.Province!.Trim());
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>workplace_type comes through as one of "onsite", "hybrid", "remote".
    /// Map to the project's hyphenated convention.</summary>
    private static string NormalizeRemote(string? workplaceType)
    {
        if (string.IsNullOrEmpty(workplaceType)) return "unknown";
        var v = workplaceType.ToLowerInvariant();
        if (v.Contains("remote")) return "remote";
        if (v.Contains("hybrid")) return "hybrid";
        if (v.Contains("onsite") || v.Contains("on-site") || v.Contains("on_site")) return "on-site";
        return "unknown";
    }

    /// <summary>employment_type is snake_case ("permanent_full_time", "contract", "part_time", etc.)</summary>
    private static string NormalizeEmployment(string? employmentType)
    {
        if (string.IsNullOrEmpty(employmentType)) return "unknown";
        var v = employmentType.ToLowerInvariant();
        if (v.Contains("full"))                              return "full-time";
        if (v.Contains("part"))                              return "part-time";
        if (v.Contains("contract") || v.Contains("temp") || v.Contains("freelance")) return "contract";
        return "unknown";
    }

    /// <summary>Pinpoint exposes salary as min/max numbers with a separate currency + frequency.
    /// Honor <c>compensation_visible</c>: when false, the company has set a range but doesn't
    /// want it shown publicly — we treat that as "no salary" rather than leaking it.</summary>
    private static (int? Min, int? Max, string? Currency, string? Period) ExtractSalary(Posting j)
    {
        if (j.CompensationVisible != true) return (null, null, null, null);
        var min = j.CompensationMinimum.HasValue ? (int?)Math.Round(j.CompensationMinimum.Value) : null;
        var max = j.CompensationMaximum.HasValue ? (int?)Math.Round(j.CompensationMaximum.Value) : null;
        if (min is null && max is null) return (null, null, null, null);
        var period = j.CompensationFrequency?.ToLowerInvariant() switch
        {
            "year"  => "year",
            "month" => "month",
            "hour"  => "hour",
            _       => null,
        };
        return (min, max, j.CompensationCurrency?.ToUpperInvariant(), period);
    }

    private sealed class Response
    {
        [JsonPropertyName("data")] public List<Posting>? Data { get; set; }
    }

    private sealed class Posting
    {
        [JsonPropertyName("id")]                    public string? Id { get; set; }
        [JsonPropertyName("title")]                 public string? Title { get; set; }
        [JsonPropertyName("url")]                   public string? Url { get; set; }
        [JsonPropertyName("description")]           public string? Description { get; set; }
        [JsonPropertyName("employment_type")]       public string? EmploymentType { get; set; }
        [JsonPropertyName("workplace_type")]        public string? WorkplaceType { get; set; }
        [JsonPropertyName("compensation_minimum")]  public double? CompensationMinimum { get; set; }
        [JsonPropertyName("compensation_maximum")]  public double? CompensationMaximum { get; set; }
        [JsonPropertyName("compensation_currency")] public string? CompensationCurrency { get; set; }
        [JsonPropertyName("compensation_frequency")] public string? CompensationFrequency { get; set; }
        [JsonPropertyName("compensation_visible")]  public bool? CompensationVisible { get; set; }
        [JsonPropertyName("location")]              public LocationObj? Location { get; set; }
        [JsonPropertyName("job")]                   public JobObj? Job { get; set; }
    }

    private sealed class LocationObj
    {
        [JsonPropertyName("name")]     public string? Name { get; set; }
        [JsonPropertyName("city")]     public string? City { get; set; }
        [JsonPropertyName("province")] public string? Province { get; set; }
    }

    private sealed class JobObj
    {
        [JsonPropertyName("department")] public NamedObj? Department { get; set; }
    }

    private sealed class NamedObj
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }
}
