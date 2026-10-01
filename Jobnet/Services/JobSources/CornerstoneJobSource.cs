using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Services.ApiUsage;
using Jobnet.Services.RateLimit;

namespace Jobnet.Services.JobSources;

/// <summary>
/// Cornerstone OnDemand (csod.com) career sites, e.g.
/// <c>seequent.csod.com/ux/ats/careersite/1/home?c=seequent</c>. The page is an SPA, but its
/// server-rendered HTML carries an anonymous bearer <c>token</c> and the tenant's regional API
/// base (<c>cloud</c>, e.g. <c>https://uk.api.csod.com/</c>). The SPA then POSTs to
/// <c>{cloud}rec-job-search/external/jobs</c>, which returns the full requisition list with
/// title, locations and description — so one page GET + one POST per 100 jobs, no browser.
///
/// <c>ats_slug</c> is the tenant (<c>seequent</c>), or <c>tenant/siteId</c> when the career site
/// isn't site 1. <c>careerSitePageId</c> is required by the API — without it the search silently
/// returns zero results.
/// </summary>
public sealed class CornerstoneJobSource : IJobSource
{
    public const string Provider = "ats_cornerstone";
    public string AtsType => "cornerstone";

    private const int PageSize = 100;
    // Same safety cap the other paginated adapters use — throw rather than silently truncate.
    private const int MaxPages = 20;
    private const string PlaceholderTitle = "ENTER DISPLAY JOB TITLE";

    private static readonly Regex TokenRe = new(@"""token""\s*:\s*""(?<v>[^""]+)""", RegexOptions.Compiled);
    private static readonly Regex CloudRe = new(@"""cloud""\s*:\s*""(?<v>https://[^""]+)""", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly IApiUsageTracker _usage;
    private readonly IRateLimiter _rateLimiter;

    public CornerstoneJobSource(HttpClient http, IApiUsageTracker usage, IRateLimiter rateLimiter)
    {
        _http = http;
        _usage = usage;
        _rateLimiter = rateLimiter;
    }

    /// <summary>Splits <c>tenant</c> or <c>tenant/siteId</c>. Site defaults to 1.</summary>
    public static (string Tenant, int SiteId) ParseSlug(string slug)
    {
        var parts = slug.Trim().Split('/', 2);
        var site = parts.Length == 2 && int.TryParse(parts[1], out var s) && s > 0 ? s : 1;
        return (parts[0].ToLowerInvariant(), site);
    }

    public static string CareerSiteUrl(string tenant, int siteId) =>
        $"https://{tenant}.csod.com/ux/ats/careersite/{siteId}/home?c={Uri.EscapeDataString(tenant)}";

    public async Task<IReadOnlyList<RawJobPosting>> FetchAsync(string slug, CancellationToken ct = default)
    {
        var (tenant, siteId) = ParseSlug(slug);
        var (token, cloud) = await GetSessionAsync(tenant, siteId, ct);

        var results = new List<RawJobPosting>();
        for (var page = 1; ; page++)
        {
            if (page > MaxPages)
                throw new InvalidOperationException(
                    $"Cornerstone '{slug}': more than {MaxPages * PageSize} postings — page cap hit, refusing to truncate.");

            await _rateLimiter.WaitAsync(Provider, ct);
            _usage.RecordCall(Provider);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{cloud}rec-job-search/external/jobs")
            {
                Content = JsonContent.Create(new
                {
                    careerSiteId = siteId,
                    careerSitePageId = siteId,
                    pageNumber = page,
                    pageSize = PageSize,
                    searchText = "",
                }),
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Cornerstone search HTTP {(int)resp.StatusCode} for '{slug}'",
                                               null, resp.StatusCode);

            var payload = await resp.Content.ReadFromJsonAsync<Response>(cancellationToken: ct);
            var batch = ParseResponse(payload, tenant, siteId);
            results.AddRange(batch);

            var total = payload?.Data?.TotalCount ?? 0;
            if (batch.Count == 0 || results.Count >= total) break;
        }
        return results;
    }

    /// <summary>GETs the career site page and pulls the anonymous token + regional API base.</summary>
    private async Task<(string Token, string Cloud)> GetSessionAsync(string tenant, int siteId, CancellationToken ct)
    {
        await _rateLimiter.WaitAsync(Provider, ct);
        _usage.RecordCall(Provider);

        using var resp = await _http.GetAsync(CareerSiteUrl(tenant, siteId), ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Cornerstone career site HTTP {(int)resp.StatusCode} for '{tenant}'",
                                           null, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync(ct);
        return ExtractSession(html)
            ?? throw new InvalidOperationException($"Cornerstone '{tenant}': no token/cloud in career site page");
    }

    /// <summary>Pulled out for unit testing. Null when either value is missing.</summary>
    public static (string Token, string Cloud)? ExtractSession(string html)
    {
        var token = TokenRe.Match(html);
        var cloud = CloudRe.Match(html);
        if (!token.Success || !cloud.Success) return null;
        var c = cloud.Groups["v"].Value;
        return (token.Groups["v"].Value, c.EndsWith('/') ? c : c + "/");
    }

    /// <summary>Pulled out for unit testing — same shape FetchAsync returns per page.</summary>
    public static IReadOnlyList<RawJobPosting> ParseResponse(Response? payload, string tenant, int siteId)
    {
        var reqs = payload?.Data?.Requisitions ?? new();
        var results = new List<RawJobPosting>(reqs.Count);
        foreach (var r in reqs)
        {
            if (r.RequisitionId is null || string.IsNullOrWhiteSpace(r.DisplayJobTitle)) continue;
            // Cornerstone's untouched requisition template — published before HR filled it in
            // (description is literally "Use Job Ad Template"). Not a real posting yet.
            if (r.DisplayJobTitle.Trim().Equals(PlaceholderTitle, StringComparison.OrdinalIgnoreCase)) continue;

            var locs = new List<string>();
            foreach (var l in r.Locations ?? new())
            {
                var s = FormatLocation(l);
                if (s is not null) locs.Add(s);
            }

            results.Add(new RawJobPosting
            {
                NativeId = r.RequisitionId.Value.ToString(),
                Title = FixMojibake(r.DisplayJobTitle!.Trim()),
                Url = $"https://{tenant}.csod.com/ux/ats/careersite/{siteId}/home/requisition/{r.RequisitionId}?c={Uri.EscapeDataString(tenant)}",
                Location = locs.Count > 0 ? locs[0] : null,
                SecondaryLocations = locs.Count > 1 ? locs.GetRange(1, locs.Count - 1) : null,
                RemoteType = "unknown",
                EmploymentType = "unknown",
                DescriptionSnippet = SnippetCleaner.Clean(FixMojibake(r.ExternalDescription), maxChars: 500),
            });
        }
        return results;
    }

    private static string? FormatLocation(Location l)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(l.City)) parts.Add(l.City!.Trim());
        if (!string.IsNullOrWhiteSpace(l.State)) parts.Add(l.State!.Trim());
        if (!string.IsNullOrWhiteSpace(l.Country)) parts.Add(l.Country!.Trim());
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>Cornerstone's descriptions come back double-encoded (UTF-8 bytes decoded as
    /// Windows-1252), so "’" arrives as "â€™" and "·" as "Â·". Reverse it only when the tell-tale
    /// sequences are present, and keep the original if the round-trip doesn't produce clean UTF-8.</summary>
    public static string? FixMojibake(string? s)
    {
        if (string.IsNullOrEmpty(s) || (s.IndexOf('Â') < 0 && s.IndexOf('â') < 0 && s.IndexOf('Ã') < 0))
            return s;
        var bytes = new byte[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            var b = ToCp1252Byte(s[i]);
            if (b is null) return s;   // a char 1252 can't produce — not mojibake, leave it alone
            bytes[i] = b.Value;
        }
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return s;
        }
    }

    // The 0x80–0x9F block is where Windows-1252 differs from Latin-1; everything else maps 1:1.
    private static readonly Dictionary<char, byte> Cp1252High = new()
    {
        ['€'] = 0x80, ['‚'] = 0x82, ['ƒ'] = 0x83, ['„'] = 0x84, ['…'] = 0x85, ['†'] = 0x86, ['‡'] = 0x87,
        ['ˆ'] = 0x88, ['‰'] = 0x89, ['Š'] = 0x8A, ['‹'] = 0x8B, ['Œ'] = 0x8C, ['Ž'] = 0x8E,
        ['‘'] = 0x91, ['’'] = 0x92, ['“'] = 0x93, ['”'] = 0x94, ['•'] = 0x95, ['–'] = 0x96, ['—'] = 0x97,
        ['˜'] = 0x98, ['™'] = 0x99, ['š'] = 0x9A, ['›'] = 0x9B, ['œ'] = 0x9C, ['ž'] = 0x9E, ['Ÿ'] = 0x9F,
    };

    private static byte? ToCp1252Byte(char c)
    {
        if (c < 0x80 || (c >= 0xA0 && c <= 0xFF)) return (byte)c;
        if (c >= 0x80 && c <= 0x9F) return (byte)c;   // undefined 1252 slots pass through as C1 controls
        return Cp1252High.TryGetValue(c, out var b) ? b : null;
    }

    public sealed class Response
    {
        [JsonPropertyName("data")] public DataObj? Data { get; set; }
    }

    public sealed class DataObj
    {
        [JsonPropertyName("totalCount")]   public int TotalCount { get; set; }
        [JsonPropertyName("requisitions")] public List<Requisition>? Requisitions { get; set; }
    }

    public sealed class Requisition
    {
        [JsonPropertyName("requisitionId")]       public long? RequisitionId { get; set; }
        [JsonPropertyName("displayJobTitle")]     public string? DisplayJobTitle { get; set; }
        [JsonPropertyName("externalDescription")] public string? ExternalDescription { get; set; }
        [JsonPropertyName("locations")]           public List<Location>? Locations { get; set; }
    }

    public sealed class Location
    {
        [JsonPropertyName("city")]    public string? City { get; set; }
        [JsonPropertyName("state")]   public string? State { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
    }
}
