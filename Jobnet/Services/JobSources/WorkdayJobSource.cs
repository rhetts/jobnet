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
/// Workday's public job-search JSON endpoint. Workday is the dominant enterprise ATS — Aritzia,
/// large retailers, banks, and many Fortune 500 companies use it. Pattern:
/// <code>
/// POST https://{tenant}.wd{N}.myworkdayjobs.com/wday/cxs/{tenant}/{site}/jobs
/// Body: {"appliedFacets":{}, "limit":20, "offset":0, "searchText":""}
/// Response: { "total": N, "jobPostings": [
///     { "title": "...", "externalPath": "/job/.../R0022014",
///       "locationsText": "...", "postedOn": "Posted Today",
///       "bulletFields": ["Seattle (Market/Region)", "R0022014"] }, ... ] }
/// </code>
///
/// Slug format: <c>{tenant}.wd{N}.myworkdayjobs.com/{site}</c> — e.g. <c>aritzia.wd3.myworkdayjobs.com/External</c>.
/// We keep the full host + site as the slug because Workday tenants live on different "wdN"
/// data centers (wd3, wd5, wd105, etc.) and a tenant can publish to multiple sites (typically
/// "External", but some have brand-specific names). The site path is what comes after the host.
///
/// Pagination: Workday returns at most 20 per request. We loop with increasing offset until
/// we've consumed <c>total</c> or hit a safety cap. For a company like Aritzia (457 jobs total)
/// that's ~23 round trips per refresh — still fast (50–200ms each) and dwarfed by Playwright's
/// 3–30s on the AI-extract path.
/// </summary>
public sealed class WorkdayJobSource : IJobSource
{
    public const string Provider = "ats_workday";
    public string AtsType => "workday";

    /// <summary>Workday caps page size at 20. Asking for more is silently truncated.</summary>
    private const int PageLimit = 20;

    /// <summary>Hard ceiling on pagination to bound a misconfigured tenant. The biggest known
    /// public Workday boards are ~5K postings; we'd hit the ceiling well before any of them.</summary>
    private const int MaxPages = 50;

    private readonly HttpClient _http;
    private readonly IApiUsageTracker _usage;
    private readonly IRateLimiter _rateLimiter;

    public WorkdayJobSource(HttpClient http, IApiUsageTracker usage, IRateLimiter rateLimiter)
    {
        _http = http;
        _usage = usage;
        _rateLimiter = rateLimiter;
    }

    public async Task<IReadOnlyList<RawJobPosting>> FetchAsync(string slug, CancellationToken ct = default)
    {
        // Slug format: "{tenant}.wd{N}.myworkdayjobs.com/{site}". Split on the first '/' so the
        // site segment can itself contain dashes / underscores / digits.
        var firstSlash = slug.IndexOf('/');
        if (firstSlash <= 0 || firstSlash == slug.Length - 1)
            throw new ArgumentException(
                $"Workday slug must be '{{host}}/{{site}}' (e.g. aritzia.wd3.myworkdayjobs.com/External); got '{slug}'");
        var host = slug[..firstSlash];
        var site = slug[(firstSlash + 1)..];

        // tenant = subdomain before .wdN
        var dot = host.IndexOf('.');
        if (dot <= 0)
            throw new ArgumentException($"Workday slug host must have a tenant subdomain; got '{host}'");
        var tenant = host[..dot];

        var endpoint = $"https://{host}/wday/cxs/{tenant}/{site}/jobs";
        var siteBase = $"https://{host}/{site}";

        var all = new List<RawJobPosting>();
        var offset = 0;
        for (var page = 0; page < MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            await _rateLimiter.WaitAsync(Provider, ct);
            _usage.RecordCall(Provider);

            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(new SearchRequest
                {
                    AppliedFacets = new System.Collections.Generic.Dictionary<string, object>(),
                    Limit = PageLimit,
                    Offset = offset,
                    SearchText = "",
                }),
            };
            req.Headers.Add("Accept", "application/json");

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Workday HTTP {(int)resp.StatusCode} for slug '{slug}' (page {page + 1})");

            var payload = await resp.Content.ReadFromJsonAsync<Response>(cancellationToken: ct);
            var batch = ParseBatch(payload, siteBase);
            all.AddRange(batch);

            var total = payload?.Total ?? 0;
            offset += PageLimit;
            // Stop when we've reached `total`, or when the batch was short (Workday's signal
            // there are no more pages), or when an empty page comes back.
            if (batch.Count == 0 || offset >= total) break;
        }
        return all;
    }

    /// <summary>Pulled out for unit testing — parse one already-deserialised page into postings.
    /// <paramref name="siteBase"/> is the URL the postings' externalPath is relative to.</summary>
    public static IReadOnlyList<RawJobPosting> ParseBatch(Response? payload, string siteBase)
    {
        var items = payload?.JobPostings ?? new();
        var results = new List<RawJobPosting>(items.Count);
        foreach (var j in items)
        {
            if (string.IsNullOrWhiteSpace(j.Title) || string.IsNullOrWhiteSpace(j.ExternalPath)) continue;

            // The stable id is the trailing token of externalPath (e.g. "R0022014"). It also
            // appears in bulletFields[1] but the path is the canonical source — bulletFields
            // ordering varies across tenants.
            var nativeId = ExtractNativeId(j.ExternalPath!, j.BulletFields);
            if (string.IsNullOrWhiteSpace(nativeId)) continue;

            results.Add(new RawJobPosting
            {
                NativeId = nativeId,
                Title = j.Title!,
                Url = siteBase.TrimEnd('/') + j.ExternalPath,
                Location = j.LocationsText,
                RemoteType = GuessRemoteType(j.LocationsText),
                EmploymentType = "unknown",   // Workday's search results don't include this; the
                                              // per-posting detail page does. Leaving as unknown
                                              // keeps the refresh cheap.
                Department = null,
                DescriptionSnippet = null,
            });
        }
        return results;
    }

    private static string ExtractNativeId(string externalPath, List<string>? bulletFields)
    {
        // Try the path tail first: /job/Some-City/Title_R0022014 → "R0022014"
        var lastSlash = externalPath.LastIndexOf('/');
        if (lastSlash >= 0 && lastSlash < externalPath.Length - 1)
        {
            var tail = externalPath[(lastSlash + 1)..];
            var lastUnder = tail.LastIndexOf('_');
            if (lastUnder >= 0 && lastUnder < tail.Length - 1)
                return tail[(lastUnder + 1)..];
            return tail;
        }
        // Fallback to bulletFields if present — many tenants surface the id there.
        if (bulletFields is { Count: > 0 })
            foreach (var b in bulletFields)
                if (!string.IsNullOrWhiteSpace(b) && b.StartsWith("R", StringComparison.OrdinalIgnoreCase))
                    return b;
        // Final fallback: use the whole externalPath. The Upsert layer dedups by hash anyway.
        return externalPath;
    }

    private static string GuessRemoteType(string? locationsText)
    {
        if (string.IsNullOrEmpty(locationsText)) return "unknown";
        var l = locationsText.ToLowerInvariant();
        if (l.Contains("remote")) return "remote";
        if (l.Contains("hybrid")) return "hybrid";
        return "on-site";
    }

    public sealed class SearchRequest
    {
        [JsonPropertyName("appliedFacets")] public Dictionary<string, object> AppliedFacets { get; set; } = new();
        [JsonPropertyName("limit")]         public int Limit { get; set; }
        [JsonPropertyName("offset")]        public int Offset { get; set; }
        [JsonPropertyName("searchText")]    public string SearchText { get; set; } = "";
    }

    public sealed class Response
    {
        [JsonPropertyName("total")]       public int? Total { get; set; }
        [JsonPropertyName("jobPostings")] public List<Posting>? JobPostings { get; set; }
    }

    public sealed class Posting
    {
        [JsonPropertyName("title")]         public string? Title { get; set; }
        [JsonPropertyName("externalPath")]  public string? ExternalPath { get; set; }
        [JsonPropertyName("locationsText")] public string? LocationsText { get; set; }
        [JsonPropertyName("postedOn")]      public string? PostedOn { get; set; }
        [JsonPropertyName("bulletFields")]  public List<string>? BulletFields { get; set; }
    }
}
