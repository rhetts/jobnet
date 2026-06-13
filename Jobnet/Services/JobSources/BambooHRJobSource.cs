using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Services.ApiUsage;
using Jobnet.Services.RateLimit;

namespace Jobnet.Services.JobSources;

/// <summary>
/// BambooHR's public careers JSON endpoint. Every customer gets a subdomain like
/// <c>itblueprint.bamboohr.com</c>, and that subdomain serves a public job list at
/// <c>/careers/list</c> returning a stable JSON shape:
/// <code>
/// { "meta": ..., "result": [
///     { "id": "18", "jobOpeningName": "...", "departmentLabel": "...",
///       "employmentStatusLabel": "Full-Time", "location": { "city": "...", "state": "..." },
///       "isRemote": true|false|null, "locationType": "..." }, ... ] }
/// </code>
/// Slug = the subdomain only (e.g. <c>itblueprint</c>). Stored in <c>companies.ats_slug</c>.
///
/// BambooHR powers many small/mid Vancouver companies that today fall through to AI extract
/// because their careers page embeds the BambooHR widget via JS and our static HTML scan
/// sees nothing useful. With this adapter, detection-by-marker + a direct API call gives us
/// fast, complete extraction for every BambooHR customer.
/// </summary>
public sealed class BambooHRJobSource : IJobSource
{
    public const string Provider = "ats_bamboohr";
    public string AtsType => "bamboohr";

    private readonly HttpClient _http;
    private readonly IApiUsageTracker _usage;
    private readonly IRateLimiter _rateLimiter;

    public BambooHRJobSource(HttpClient http, IApiUsageTracker usage, IRateLimiter rateLimiter)
    {
        _http = http;
        _usage = usage;
        _rateLimiter = rateLimiter;
    }

    public async Task<IReadOnlyList<RawJobPosting>> FetchAsync(string slug, CancellationToken ct = default)
    {
        // The slug is just the subdomain — e.g. "itblueprint" → itblueprint.bamboohr.com.
        var url = $"https://{Uri.EscapeDataString(slug)}.bamboohr.com/careers/list";

        await _rateLimiter.WaitAsync(Provider, ct);
        _usage.RecordCall(Provider);

        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"BambooHR HTTP {(int)resp.StatusCode} for slug '{slug}'");

        var payload = await resp.Content.ReadFromJsonAsync<Response>(cancellationToken: ct);
        return ParseResponse(payload, slug);
    }

    /// <summary>Pulled out for unit testing — accepts an already-deserialised <see cref="Response"/>
    /// (captured JSON fixture) and returns the same shape FetchAsync would.</summary>
    public static IReadOnlyList<RawJobPosting> ParseResponse(Response? payload, string slug)
    {
        var items = payload?.Result ?? new();
        var results = new List<RawJobPosting>(items.Count);
        foreach (var j in items)
        {
            if (string.IsNullOrEmpty(j.Id) || string.IsNullOrWhiteSpace(j.JobOpeningName)) continue;

            results.Add(new RawJobPosting
            {
                NativeId = j.Id!,
                Title = j.JobOpeningName!,
                Url = $"https://{slug}.bamboohr.com/careers/{j.Id}",
                Location = FormatLocation(j.Location),
                RemoteType = ResolveRemoteType(j),
                EmploymentType = NormalizeEmployment(j.EmploymentStatusLabel),
                Department = j.DepartmentLabel,
                DescriptionSnippet = null,   // not in the list endpoint; the per-job page has it.
            });
        }
        return results;
    }

    private static string? FormatLocation(LocationObj? loc)
    {
        if (loc is null) return null;
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(loc.City))  parts.Add(loc.City!);
        if (!string.IsNullOrWhiteSpace(loc.State)) parts.Add(loc.State!);
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>Map BambooHR's three signals (isRemote bool, locationType code, and the parent
    /// "remote" string when present) onto the DB's <c>on-site|hybrid|remote|unknown</c> enum.
    /// locationType="3" is BambooHR's "remote" marker; "2" is on-site; "4" is hybrid (newer).</summary>
    private static string ResolveRemoteType(Posting j)
    {
        if (j.IsRemote == true) return "remote";
        return j.LocationType switch
        {
            "2" => "on-site",
            "3" => "remote",
            "4" => "hybrid",
            _   => "unknown",
        };
    }

    private static string NormalizeEmployment(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "unknown";
        var l = label.ToLowerInvariant();
        if (l.Contains("full"))      return "full-time";
        if (l.Contains("part"))      return "part-time";
        if (l.Contains("contract") || l.Contains("temp")) return "contract";
        return "unknown";
    }

    public sealed class Response
    {
        [JsonPropertyName("result")] public List<Posting>? Result { get; set; }
    }

    public sealed class Posting
    {
        // BambooHR returns id as a string in some accounts, integer in others — handle both.
        [JsonPropertyName("id")]                       public string? Id { get; set; }
        [JsonPropertyName("jobOpeningName")]           public string? JobOpeningName { get; set; }
        [JsonPropertyName("departmentLabel")]          public string? DepartmentLabel { get; set; }
        [JsonPropertyName("employmentStatusLabel")]    public string? EmploymentStatusLabel { get; set; }
        [JsonPropertyName("location")]                 public LocationObj? Location { get; set; }
        [JsonPropertyName("isRemote")]                 public bool? IsRemote { get; set; }
        [JsonPropertyName("locationType")]             public string? LocationType { get; set; }
    }

    public sealed class LocationObj
    {
        [JsonPropertyName("city")]  public string? City { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
    }
}
