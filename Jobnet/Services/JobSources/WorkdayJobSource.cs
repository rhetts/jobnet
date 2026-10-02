using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Services.ApiUsage;
using Jobnet.Services.Location;
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
public sealed class WorkdayJobSource : IJobSource, IKeywordFilteredJobSource
{
    public const string Provider = "ats_workday";
    public string AtsType => "workday";

    /// <summary>Workday caps page size at 20. Asking for more is silently truncated.</summary>
    private const int PageLimit = 20;

    /// <summary>Hard ceiling on pagination to bound a misconfigured tenant (150 pages = 3000
    /// postings; Mastercard alone is ~1050). Exceeding it throws rather than returning a
    /// truncated list.</summary>
    private const int MaxPages = 150;

    private readonly HttpClient _http;
    private readonly IApiUsageTracker _usage;
    private readonly IRateLimiter _rateLimiter;

    public WorkdayJobSource(HttpClient http, IApiUsageTracker usage, IRateLimiter rateLimiter)
    {
        _http = http;
        _usage = usage;
        _rateLimiter = rateLimiter;
    }

    public Task<IReadOnlyList<RawJobPosting>> FetchAsync(string slug, CancellationToken ct = default)
        => FetchCoreAsync(slug, "", ct);

    /// <summary>Only postings matching <paramref name="keyword"/> in Workday's own search (title and
    /// description) — for a company whose jobs share a parent's tenant.</summary>
    public Task<IReadOnlyList<RawJobPosting>> FetchAsync(string slug, string keyword, CancellationToken ct = default)
        => FetchCoreAsync(slug, keyword.Trim(), ct);

    private async Task<IReadOnlyList<RawJobPosting>> FetchCoreAsync(string slug, string searchText, CancellationToken ct)
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

        // First, an unfiltered page-0 request: it carries the tenant's facet tree, which is the
        // only way to learn its location facet ids. If it has one, re-query filtered to the
        // Vancouver-area values so we don't page through a global board (Mastercard: ~1050
        // postings, ~17 in the area) just to drop almost everything at the location gate.
        var first = await PostPageAsync(endpoint, slug, new Dictionary<string, object>(), searchText, 0, 0, ct);
        var areaFacet = PickAreaLocationFacet(first?.Facets);
        IReadOnlyList<string>? inAreaLocations = null;
        var applied = new Dictionary<string, object>();
        if (areaFacet is { } f)
        {
            // Tenant has a location facet but nothing in the area: a real empty result.
            if (f.Ids.Count == 0) return new List<RawJobPosting>();
            applied[f.Parameter] = f.Ids;
            inAreaLocations = f.Descriptors;
            first = null; // page 0 must be re-fetched with the filter applied
        }

        var all = new List<RawJobPosting>();
        var offset = 0;
        var total = 0;
        for (var page = 0; ; page++)
        {
            // Hitting the ceiling means we'd return a truncated list, and the refresher would
            // close every posting past it. Fail the fetch instead so nothing gets closed.
            if (page >= MaxPages)
                throw new InvalidOperationException(
                    $"Workday slug '{slug}' exceeded {MaxPages} pages ({MaxPages * PageLimit} postings); result would be truncated");

            var payload = page == 0 && first is not null
                ? first
                : await PostPageAsync(endpoint, slug, applied, searchText, offset, page, ct);
            var batch = ParseBatch(payload, siteBase, inAreaLocations);
            all.AddRange(batch);

            // Workday only reports `total` on the first page (offset=0) -- later pages return
            // total=0 -- so capture it once. Reading it per page stopped every tenant at 40.
            if (page == 0) total = payload?.Total ?? 0;
            offset += PageLimit;
            // Stop when we've reached `total`, or when an empty page comes back.
            if (batch.Count == 0 || offset >= total) break;
        }
        return all;
    }

    private async Task<Response?> PostPageAsync(string endpoint, string slug, Dictionary<string, object> appliedFacets,
                                                string searchText, int offset, int page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await _rateLimiter.WaitAsync(Provider, ct);
        _usage.RecordCall(Provider);

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new SearchRequest
            {
                AppliedFacets = appliedFacets,
                Limit = PageLimit,
                Offset = offset,
                SearchText = searchText,
            }),
        };
        req.Headers.Add("Accept", "application/json");

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Workday HTTP {(int)resp.StatusCode} for slug '{slug}' (page {page + 1})");
        return await resp.Content.ReadFromJsonAsync<Response>(cancellationToken: ct);
    }

    /// <summary>Location facet parameters, most specific first. Most tenants expose
    /// <c>locations</c> (nested under <c>locationMainGroup</c>); some (TELUS/LifeWorks) only a
    /// flat <c>City</c>. Country/region facets are deliberately not used: too coarse to match
    /// the per-city location gate.</summary>
    private static readonly string[] LocationFacetParams = { "locations", "City" };

    /// <summary>Pulled out for unit testing. Finds the tenant's city-level location facet and
    /// returns the values that pass <see cref="LocationMatcher.IsVancouverArea"/>, the same rule
    /// the refresher's location gate applies to each posting, so filtering server-side keeps the
    /// same set (plus multi-location postings the gate can't see into). Returns null when the
    /// tenant has no recognisable location facet, meaning: don't filter.</summary>
    public static (string Parameter, List<string> Ids, List<string> Descriptors)? PickAreaLocationFacet(List<Facet>? facets)
    {
        if (facets is null) return null;
        var flat = new List<Facet>();
        void Walk(List<Facet>? fs)
        {
            if (fs is null) return;
            foreach (var f in fs)
            {
                if (!string.IsNullOrEmpty(f.FacetParameter)) flat.Add(f);
                Walk(f.Values);
            }
        }
        Walk(facets);

        foreach (var param in LocationFacetParams)
        {
            var facet = flat.FirstOrDefault(f => f.FacetParameter == param
                                              && f.Values is { Count: > 0 }
                                              && f.Values.All(v => v.Id is not null));
            if (facet is null) continue;
            var matches = facet.Values!
                .Where(v => !string.IsNullOrWhiteSpace(v.Descriptor) && LocationMatcher.IsVancouverArea(v.Descriptor))
                .ToList();
            return (param, matches.Select(v => v.Id!).ToList(), matches.Select(v => v.Descriptor!).ToList());
        }
        return null;
    }

    /// <summary>Pulled out for unit testing — parse one already-deserialised page into postings.
    /// <paramref name="siteBase"/> is the URL the postings' externalPath is relative to.
    /// <paramref name="inAreaLocations"/>, when set, are the Vancouver-area location facet values
    /// the page was filtered by: a multi-site posting ("2 Locations", "X, More...") gets them as
    /// <see cref="RawJobPosting.SecondaryLocations"/> so the location gate keeps it, since the
    /// filter already guarantees it's in one of them.</summary>
    public static IReadOnlyList<RawJobPosting> ParseBatch(Response? payload, string siteBase,
                                                          IReadOnlyList<string>? inAreaLocations = null)
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
                SecondaryLocations = inAreaLocations is not null && IsMultiLocation(j.LocationsText)
                    ? inAreaLocations : null,
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

    private static readonly Regex MultiLocationRe =
        // "2 Locations" on most tenants; Motorola shows the first one plus "More..." instead.
        new(@"^\s*\d+\s+Locations\s*$|,\s*More\.\.\.\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsMultiLocation(string? locationsText) =>
        locationsText is not null && MultiLocationRe.IsMatch(locationsText);

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
        [JsonPropertyName("facets")]      public List<Facet>? Facets { get; set; }
    }

    /// <summary>One node of Workday's facet tree. Group nodes carry <c>facetParameter</c> +
    /// child <c>values</c>; leaf values carry <c>id</c> + <c>descriptor</c> + <c>count</c>.</summary>
    public sealed class Facet
    {
        [JsonPropertyName("facetParameter")] public string? FacetParameter { get; set; }
        [JsonPropertyName("descriptor")]     public string? Descriptor { get; set; }
        [JsonPropertyName("id")]             public string? Id { get; set; }
        [JsonPropertyName("count")]          public int? Count { get; set; }
        [JsonPropertyName("values")]         public List<Facet>? Values { get; set; }
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
