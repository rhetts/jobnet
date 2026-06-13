using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Services.ApiUsage;
using Jobnet.Services.RateLimit;

namespace Jobnet.Services.JobSources;

/// <summary>
/// Workable's public widget API. Every customer board is reachable at
/// <c>https://apply.workable.com/api/v1/widget/accounts/{slug}</c> with no auth.
/// Returns a single payload (no pagination — even tenants with 100+ jobs come back in one
/// response):
/// <code>
/// { "name": "...", "description": "...", "jobs": [
///     { "shortcode": "B2D9916D4E", "title": "...", "employment_type": "Full-time",
///       "telecommuting": true|false, "department": "...", "url": "https://apply.workable.com/j/{shortcode}",
///       "country": "...", "city": "...", "state": "...",
///       "locations": [ { "country": "...", "city": "...", "region": "..." } ] }, ... ] }
/// </code>
///
/// Slug is the subdomain Workable assigned — visible in their /careers redirect or in
/// embedded widget URLs on the customer's marketing site.
/// </summary>
public sealed class WorkableJobSource : IJobSource
{
    public const string Provider = "ats_workable";
    public string AtsType => "workable";

    private readonly HttpClient _http;
    private readonly IApiUsageTracker _usage;
    private readonly IRateLimiter _rateLimiter;

    public WorkableJobSource(HttpClient http, IApiUsageTracker usage, IRateLimiter rateLimiter)
    {
        _http = http;
        _usage = usage;
        _rateLimiter = rateLimiter;
    }

    public async Task<IReadOnlyList<RawJobPosting>> FetchAsync(string slug, CancellationToken ct = default)
    {
        var url = $"https://apply.workable.com/api/v1/widget/accounts/{Uri.EscapeDataString(slug)}";

        await _rateLimiter.WaitAsync(Provider, ct);
        _usage.RecordCall(Provider);

        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Workable HTTP {(int)resp.StatusCode} for slug '{slug}'");

        var payload = await resp.Content.ReadFromJsonAsync<Response>(cancellationToken: ct);
        return ParseResponse(payload);
    }

    /// <summary>Pulled out for unit testing — convert a deserialised payload to RawJobPostings.</summary>
    public static IReadOnlyList<RawJobPosting> ParseResponse(Response? payload)
    {
        var items = payload?.Jobs ?? new();
        var results = new List<RawJobPosting>(items.Count);
        foreach (var j in items)
        {
            if (string.IsNullOrWhiteSpace(j.Shortcode) || string.IsNullOrWhiteSpace(j.Title)) continue;

            results.Add(new RawJobPosting
            {
                NativeId = j.Shortcode!,
                Title = j.Title!,
                Url = j.Url ?? $"https://apply.workable.com/j/{j.Shortcode}",
                Location = FormatLocation(j),
                RemoteType = j.Telecommuting == true ? "remote" : "unknown",
                EmploymentType = NormalizeEmployment(j.EmploymentType),
                Department = j.Department,
                DescriptionSnippet = null,
            });
        }
        return results;
    }

    /// <summary>Prefer the first entry in <c>locations</c> (newer field, structured) over the flat
    /// city/state/country triple. Some tenants only emit one or the other; we accept either.</summary>
    private static string? FormatLocation(JobPosting j)
    {
        if (j.Locations is { Count: > 0 })
        {
            var loc = j.Locations[0];
            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(loc.City))    parts.Add(loc.City!);
            if (!string.IsNullOrWhiteSpace(loc.Region))  parts.Add(loc.Region!);
            if (!string.IsNullOrWhiteSpace(loc.Country)) parts.Add(loc.Country!);
            if (parts.Count > 0) return string.Join(", ", parts);
        }
        var flat = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(j.City))    flat.Add(j.City!);
        if (!string.IsNullOrWhiteSpace(j.State))   flat.Add(j.State!);
        if (!string.IsNullOrWhiteSpace(j.Country)) flat.Add(j.Country!);
        return flat.Count == 0 ? null : string.Join(", ", flat);
    }

    private static string NormalizeEmployment(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "unknown";
        var l = label.ToLowerInvariant();
        if (l.Contains("full"))                                    return "full-time";
        if (l.Contains("part"))                                    return "part-time";
        if (l.Contains("contract") || l.Contains("temp"))          return "contract";
        return "unknown";
    }

    public sealed class Response
    {
        [JsonPropertyName("jobs")] public List<JobPosting>? Jobs { get; set; }
    }

    public sealed class JobPosting
    {
        [JsonPropertyName("shortcode")]       public string? Shortcode { get; set; }
        [JsonPropertyName("title")]           public string? Title { get; set; }
        [JsonPropertyName("employment_type")] public string? EmploymentType { get; set; }
        [JsonPropertyName("telecommuting")]   public bool? Telecommuting { get; set; }
        [JsonPropertyName("department")]      public string? Department { get; set; }
        [JsonPropertyName("url")]             public string? Url { get; set; }
        [JsonPropertyName("country")]         public string? Country { get; set; }
        [JsonPropertyName("city")]            public string? City { get; set; }
        [JsonPropertyName("state")]           public string? State { get; set; }
        [JsonPropertyName("locations")]       public List<LocationObj>? Locations { get; set; }
    }

    public sealed class LocationObj
    {
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("city")]    public string? City { get; set; }
        [JsonPropertyName("region")]  public string? Region { get; set; }
    }
}
