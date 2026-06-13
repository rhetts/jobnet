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
/// Amazon careers via their public <c>amazon.jobs/en/search.json</c> endpoint. Replaces the
/// AI-extract loop that was burning 20+ Gemini calls per Amazon refresh against a 404 page —
/// the company's stored careers URL <c>amazon.jobs/en/locations/vancouver-british-columbia</c>
/// returns HTTP 404 (Amazon retired the URL structure), so the fallback path was crawling
/// dozens of cached anchor URLs and getting nothing.
///
/// API quirks worth knowing:
/// * Most filter params Amazon's UI hints at (<c>country[]</c>, <c>city[]</c>, etc.) are
///   silently ignored — the server returns 10,000 hits regardless. The two that actually
///   filter are <c>normalized_country_code[]</c> (e.g. <c>CAN</c>) and
///   <c>normalized_state_name[]</c> (e.g. <c>British Columbia</c>). We use the latter because
///   it's the tightest match for Vancouver-area roles (154 jobs vs 363 country-wide).
/// * <c>hits</c> in the response is a capped global count, not a filtered count — don't trust
///   it as the total available. Page until the returned <c>jobs[]</c> is short or empty.
/// * No auth required. No rate limit documented; we still go through <see cref="IRateLimiter"/>
///   under the "ats_amazon" provider key.
///
/// Slug convention for the company row: a comma-separated key=value list of filter params
/// (URL-encoded). Examples:
///   <c>state=British Columbia</c>     — Vancouver-area + Burnaby/Richmond/etc.
///   <c>country=CAN</c>                — all Canada
///   <c>state=British Columbia,team=Devices &amp; Services</c>  — multiple filters
/// Order doesn't matter. Whitespace around keys/values is trimmed.
/// </summary>
public sealed class AmazonJobSource : IJobSource
{
    public const string Provider = "ats_amazon";
    public string AtsType => "amazon";

    /// <summary>Amazon's JSON endpoint caps results per request. 100 is the largest value the
    /// website itself requests, and going higher silently truncates. With 154 BC postings, two
    /// pages cover everything.</summary>
    private const int PageLimit = 100;

    /// <summary>Hard ceiling on pagination — same shape as <see cref="WorkdayJobSource"/>. With
    /// Amazon's global cap of 10,000 hits we'd never reach this; it's a runaway guard for
    /// misconfigured slugs that match the whole DB.</summary>
    private const int MaxPages = 30;

    private readonly HttpClient _http;
    private readonly IApiUsageTracker _usage;
    private readonly IRateLimiter _rateLimiter;

    public AmazonJobSource(HttpClient http, IApiUsageTracker usage, IRateLimiter rateLimiter)
    {
        _http = http;
        _usage = usage;
        _rateLimiter = rateLimiter;
    }

    public async Task<IReadOnlyList<RawJobPosting>> FetchAsync(string slug, CancellationToken ct = default)
    {
        // Parse the comma-separated slug into the filter params Amazon honours.
        var filterQuery = BuildFilterQuery(slug);
        if (string.IsNullOrEmpty(filterQuery))
            throw new ArgumentException(
                $"Amazon slug must contain at least one of state=... / country=...; got '{slug}'");

        var results = new List<RawJobPosting>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var page = 0; page < MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var offset = page * PageLimit;
            var url = $"https://www.amazon.jobs/en/search.json?{filterQuery}&result_limit={PageLimit}&offset={offset}";

            await _rateLimiter.WaitAsync(Provider, ct);
            _usage.RecordCall(Provider);

            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Amazon HTTP {(int)resp.StatusCode} for slug '{slug}' (page {page})",
                    null, resp.StatusCode);

            var payload = await resp.Content.ReadFromJsonAsync<Response>(cancellationToken: ct);
            var batch = payload?.Jobs ?? new();
            if (batch.Count == 0) break;

            foreach (var j in batch)
            {
                if (string.IsNullOrEmpty(j.Id) || string.IsNullOrEmpty(j.Title)) continue;
                // Amazon occasionally returns the same posting twice across pages (eviction during
                // pagination). Dedupe by id — id_icims would also work but id is consistently set.
                if (!seen.Add(j.Id)) continue;

                results.Add(new RawJobPosting
                {
                    NativeId = j.Id!,
                    Title = j.Title!.Trim(),
                    Url = !string.IsNullOrEmpty(j.JobPath)
                          ? $"https://www.amazon.jobs{j.JobPath}"
                          : null,
                    Location = j.NormalizedLocation ?? j.Location,
                    RemoteType = GuessRemote(j.Location, j.NormalizedLocation),
                    EmploymentType = NormalizeEmployment(j.JobScheduleType),
                    Department = j.BusinessCategory ?? j.JobCategory,
                    DescriptionSnippet = SnippetCleaner.Clean(j.DescriptionShort ?? j.Description, maxChars: 500),
                });
            }

            // If we got fewer than a full page, we're at the end. Saves a final empty request.
            if (batch.Count < PageLimit) break;
        }
        return results;
    }

    /// <summary>Convert the slug ("state=British Columbia,country=CAN,team=Devices &amp; Services")
    /// into a URL-encoded query fragment Amazon's API understands. Unknown keys are silently
    /// dropped rather than passed through — Amazon's API ignores unknown params anyway, but
    /// dropping them keeps the URL clean.</summary>
    private static string BuildFilterQuery(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return "";
        var parts = new List<string>();
        foreach (var raw in slug.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = raw.IndexOf('=');
            if (eq <= 0) continue;
            var key = raw[..eq].Trim().ToLowerInvariant();
            var value = raw[(eq + 1)..].Trim();
            if (string.IsNullOrEmpty(value)) continue;

            // Map our friendly keys onto the exact Amazon param names. We deliberately accept
            // only the params that actually filter — the others would be no-ops.
            var paramName = key switch
            {
                "state"     => "normalized_state_name[]",
                "country"   => "normalized_country_code[]",
                "city"      => "normalized_city_name[]",   // best-effort; Amazon's behaviour here is inconsistent
                "team"      => "team[]",
                "category"  => "business_category[]",
                _           => null,
            };
            if (paramName is null) continue;
            parts.Add($"{Uri.EscapeDataString(paramName)}={Uri.EscapeDataString(value)}");
        }
        return string.Join("&", parts);
    }

    private static string GuessRemote(string? location, string? normalized)
    {
        var hay = ((location ?? "") + " " + (normalized ?? "")).ToLowerInvariant();
        if (hay.Contains("virtual") || hay.Contains("remote")) return "remote";
        return "on-site";
    }

    /// <summary>Amazon publishes <c>job_schedule_type</c> as kebab-case ("full-time" / "part-time"
    /// / etc.). Normalise to the project's CHECK-constrained values.</summary>
    private static string NormalizeEmployment(string? scheduleType)
    {
        if (string.IsNullOrEmpty(scheduleType)) return "unknown";
        var v = scheduleType.ToLowerInvariant();
        if (v.Contains("full"))                                          return "full-time";
        if (v.Contains("part"))                                          return "part-time";
        if (v.Contains("contract") || v.Contains("temp") || v.Contains("seasonal")) return "contract";
        return "unknown";
    }

    public sealed class Response
    {
        [JsonPropertyName("jobs")] public List<Posting>? Jobs { get; set; }
        [JsonPropertyName("hits")] public int? Hits { get; set; }
    }

    public sealed class Posting
    {
        [JsonPropertyName("id")]                  public string? Id { get; set; }
        [JsonPropertyName("id_icims")]            public string? IdIcims { get; set; }
        [JsonPropertyName("title")]               public string? Title { get; set; }
        [JsonPropertyName("job_path")]            public string? JobPath { get; set; }
        [JsonPropertyName("location")]            public string? Location { get; set; }
        [JsonPropertyName("normalized_location")] public string? NormalizedLocation { get; set; }
        [JsonPropertyName("job_schedule_type")]   public string? JobScheduleType { get; set; }
        [JsonPropertyName("business_category")]   public string? BusinessCategory { get; set; }
        [JsonPropertyName("job_category")]        public string? JobCategory { get; set; }
        [JsonPropertyName("description")]         public string? Description { get; set; }
        [JsonPropertyName("description_short")]   public string? DescriptionShort { get; set; }
    }
}
