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
/// Eightfold.ai is an AI-driven enterprise ATS/talent platform, typically hosted on a
/// <c>{tenant}.eightfold.ai</c> subdomain (PayPal: <c>paypal.eightfold.ai</c>) — or, just as
/// commonly for very large enterprises, on their own CNAME'd domain entirely (Microsoft:
/// <c>apply.careers.microsoft.com</c>, confirmed to hit the identical API). The public careers
/// page (<c>/careers</c>) is a client-rendered SPA with no useful static HTML — but it calls a
/// real, public, unauthenticated JSON search API confirmed via captured network traffic
/// (<c>derive-parser</c>'s request log): <c>GET /api/pcsx/search?domain=...&amp;location=...</c>.
///
/// Two false leads along the way, in case someone else finds this ATS again:
/// <list type="bullet">
/// <item><c>/api/apply/v2/jobs</c> (a path pattern documented informally elsewhere online) 404s/
/// 403s here — that's not this tenant's actual endpoint.</item>
/// <item>Passing a plain city name as <c>location</c> (e.g. "Vancouver") returns zero results —
/// the API wants geocoded <c>"{lat},{lng}"</c>, matching what the real page sends once a user
/// picks a location. Confirmed Burnaby (49.2732,-123.0124) and Vancouver (49.2827,-123.1207)
/// both work with a wide <c>filter_distance</c>.</item>
/// </list>
///
/// <c>filter_include_remote=1</c> is always sent (not stored in the slug) because for a company
/// this size, "remote-eligible" postings are exactly the ones a Vancouver candidate can actually
/// take — PayPal itself only returned 3 total near Vancouver, all `remote_local`/`hybrid`, no
/// on-site BC office presence. Real, sparse data — the whole point of building this instead of
/// leaving PayPal on the AI-extract path, which had already been caught inventing fake job titles
/// for a different company when it couldn't get real content (see Clio's history).
/// </summary>
public sealed class EightfoldJobSource : IJobSource
{
    public const string Provider = "ats_eightfold";
    public string AtsType => "eightfold";

    private const int PageSize = 20;
    private const int MaxPages = 20;

    private readonly HttpClient _http;
    private readonly IApiUsageTracker _usage;
    private readonly IRateLimiter _rateLimiter;

    public EightfoldJobSource(HttpClient http, IApiUsageTracker usage, IRateLimiter rateLimiter)
    {
        _http = http;
        _usage = usage;
        _rateLimiter = rateLimiter;
    }

    /// <summary>Slug format: <c>{host}?domain={tenant-configured domain}&amp;location={lat},{lng}&amp;filter_distance={radius}</c>,
    /// e.g. <c>paypal.eightfold.ai?domain=paypal.com&amp;location=49.2827,-123.1207&amp;filter_distance=80</c>.
    /// <paramref name="host"/> is the full API host — a <c>{tenant}.eightfold.ai</c> subdomain
    /// (PayPal) or, just as often for large enterprises, their own CNAME'd domain entirely
    /// (Microsoft: <c>apply.careers.microsoft.com</c> — same <c>/api/pcsx/search</c> endpoint,
    /// confirmed empirically). <paramref name="slug"/>'s query is appended as-is; <c>start</c>/
    /// <c>num</c>/<c>query</c>/<c>filter_include_remote</c> are added per page and must not be
    /// included in the stored slug.</summary>
    public async Task<IReadOnlyList<RawJobPosting>> FetchAsync(string slug, CancellationToken ct = default)
    {
        var qIdx = slug.IndexOf('?');
        if (qIdx < 0)
            throw new ArgumentException($"Eightfold slug must be '{{host}}?domain=...&location=...'; got '{slug}'");
        var host = slug[..qIdx];
        var query = slug[(qIdx + 1)..];

        var results = new List<RawJobPosting>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var page = 0; page < MaxPages; page++)
        {
            var start = page * PageSize;
            var url = $"https://{host}/api/pcsx/search?{query}" +
                      $"&query=&start={start}&num={PageSize}&filter_include_remote=1";

            await _rateLimiter.WaitAsync(Provider, ct);
            _usage.RecordCall(Provider);

            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Eightfold HTTP {(int)resp.StatusCode} for slug '{slug}' (start {start})");

            var payload = await resp.Content.ReadFromJsonAsync<Response>(cancellationToken: ct);
            var positions = payload?.Data?.Positions ?? new();
            if (positions.Count == 0) break;

            foreach (var p in positions)
            {
                if (p.Id is null || string.IsNullOrWhiteSpace(p.Name)) continue;
                var nativeId = p.Id.Value.ToString();
                if (!seen.Add(nativeId)) continue;

                results.Add(new RawJobPosting
                {
                    NativeId = nativeId,
                    Title = p.Name!,
                    Url = string.IsNullOrWhiteSpace(p.PositionUrl) ? null : $"https://{host}{p.PositionUrl}",
                    Location = FormatLocation(p),
                    RemoteType = ResolveRemoteType(p.WorkLocationOption),
                    EmploymentType = null, // not present on the search-list payload
                    Department = p.Department,
                });
            }

            if (positions.Count < PageSize) break; // short page — last one
        }

        return results;
    }

    private static string? FormatLocation(Position p)
    {
        var locs = p.StandardizedLocations?.Count > 0 ? p.StandardizedLocations : p.Locations;
        return locs is { Count: > 0 } ? string.Join("; ", locs) : null;
    }

    private static string ResolveRemoteType(string? option)
    {
        if (string.IsNullOrWhiteSpace(option)) return "unknown";
        var o = option.ToLowerInvariant();
        if (o.Contains("remote")) return "remote";
        if (o.Contains("hybrid")) return "hybrid";
        if (o.Contains("onsite") || o.Contains("on_site")) return "on-site";
        return "unknown";
    }

    public sealed class Response
    {
        [JsonPropertyName("data")] public DataObj? Data { get; set; }
    }

    public sealed class DataObj
    {
        [JsonPropertyName("positions")] public List<Position>? Positions { get; set; }
    }

    public sealed class Position
    {
        [JsonPropertyName("id")]                     public long? Id { get; set; }
        [JsonPropertyName("name")]                    public string? Name { get; set; }
        [JsonPropertyName("department")]              public string? Department { get; set; }
        [JsonPropertyName("positionUrl")]             public string? PositionUrl { get; set; }
        [JsonPropertyName("workLocationOption")]      public string? WorkLocationOption { get; set; }
        [JsonPropertyName("locations")]               public List<string>? Locations { get; set; }
        [JsonPropertyName("standardizedLocations")]   public List<string>? StandardizedLocations { get; set; }
    }
}
