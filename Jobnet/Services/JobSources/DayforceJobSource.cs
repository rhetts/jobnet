using System;
using System.Collections.Generic;
using System.Linq;
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
/// Dayforce's own hosted career-site product (jobs.dayforcehcm.com) — used by Dayforce itself to
/// hire, and by any other company whose careers run on the same platform (Dayforce is an HCM/HR
/// vendor, and this is one of the products it sells to its customers, the same way Workday and
/// Greenhouse host postings for many unrelated companies). Reverse-engineered from a HAR capture:
/// <code>
/// GET  https://jobs.dayforcehcm.com/api/auth/csrf
///      -> {"csrfToken": "..."} + sets __Host-next-auth.csrf-token / __cf_bm cookies
/// POST https://jobs.dayforcehcm.com/api/geo/{clientNamespace}/jobposting/search
///      Header: x-csrf-token: {csrfToken}
///      Body: {"clientNamespace","jobBoardCode","cultureCode":"en-US","distanceUnit":0,"paginationStart"}
///      Response: {"jobPostings":[{"jobPostingId","jobTitle","jobDescription","hasVirtualLocation",
///                                 "postingLocations":[{"formattedAddress",...}], ...}],
///                 "maxCount","offset","count"}
/// </code>
/// This is NextAuth's standard double-submit CSRF pattern (the token must match what the cookie
/// from the GET carries) — a bare POST without it 403s. Unlike S.i. Systems, this site's bot
/// defense (Cloudflare) does not fingerprint the TLS handshake: plain HttpClient (and a
/// PowerShell/.NET Invoke-WebRequest test) both complete the flow fine, so no curl subprocess
/// needed here.
///
/// <paramref name="slug"/> is <c>"{clientNamespace}/{jobBoardCode}"</c> (e.g. "mydayforce/alljobs"),
/// mirroring the WorkdayJobSource "{host}/{site}" convention — both halves vary per company.
/// </summary>
public sealed class DayforceJobSource : IJobSource
{
    public const string Provider = "ats_dayforce";
    public string AtsType => "dayforce";

    private const int MaxPages = 30; // safety cap against a misconfigured/huge board

    private readonly HttpClient _http;
    private readonly IApiUsageTracker _usage;
    private readonly IRateLimiter _rateLimiter;

    public DayforceJobSource(HttpClient http, IApiUsageTracker usage, IRateLimiter rateLimiter)
    {
        _http = http;
        _usage = usage;
        _rateLimiter = rateLimiter;
    }

    public async Task<IReadOnlyList<RawJobPosting>> FetchAsync(string slug, CancellationToken ct = default)
    {
        var firstSlash = slug.IndexOf('/');
        if (firstSlash <= 0 || firstSlash == slug.Length - 1)
            throw new ArgumentException(
                $"Dayforce slug must be '{{clientNamespace}}/{{jobBoardCode}}' (e.g. mydayforce/alljobs); got '{slug}'");
        var clientNamespace = slug[..firstSlash];
        var jobBoardCode = slug[(firstSlash + 1)..];

        await _rateLimiter.WaitAsync(Provider, ct);
        _usage.RecordCall(Provider);
        var csrfToken = await GetCsrfTokenAsync(ct);

        var all = new List<RawJobPosting>();
        var offset = 0;

        for (var page = 0; page < MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            await _rateLimiter.WaitAsync(Provider, ct);
            _usage.RecordCall(Provider);

            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"https://jobs.dayforcehcm.com/api/geo/{clientNamespace}/jobposting/search")
            {
                Content = JsonContent.Create(new SearchRequest
                {
                    ClientNamespace = clientNamespace,
                    JobBoardCode = jobBoardCode,
                    PaginationStart = offset,
                }),
            };
            req.Headers.Add("Accept", "application/json");
            req.Headers.Add("x-csrf-token", csrfToken);
            req.Headers.Add("Origin", "https://jobs.dayforcehcm.com");
            req.Headers.Add("Referer", $"https://jobs.dayforcehcm.com/{clientNamespace}/{jobBoardCode}");

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Dayforce HTTP {(int)resp.StatusCode} for slug '{slug}' (page {page + 1})");

            var payload = await resp.Content.ReadFromJsonAsync<Response>(cancellationToken: ct);
            var batch = ParseBatch(payload, clientNamespace, jobBoardCode);
            all.AddRange(batch);

            var pageCount = payload?.Count ?? 0;
            var total = payload?.MaxCount ?? 0;
            offset += pageCount;
            if (pageCount == 0 || offset >= total) break;
        }
        return all;
    }

    /// <summary>NextAuth's double-submit CSRF token: the GET both returns the token in JSON and
    /// sets the matching cookie on the HttpClient's cookie container, so the same HttpClient
    /// instance must make both this call and the search POSTs that follow.</summary>
    private async Task<string> GetCsrfTokenAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync("https://jobs.dayforcehcm.com/api/auth/csrf", ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Dayforce HTTP {(int)resp.StatusCode} fetching CSRF token");
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("csrfToken").GetString()
               ?? throw new InvalidOperationException("Dayforce: csrf response had no csrfToken");
    }

    /// <summary>Pulled out for unit testing — convert a deserialised page into RawJobPostings.</summary>
    public static IReadOnlyList<RawJobPosting> ParseBatch(Response? payload, string clientNamespace, string jobBoardCode)
    {
        var items = payload?.JobPostings ?? new();
        var results = new List<RawJobPosting>(items.Count);
        foreach (var j in items)
        {
            if (j.JobPostingId is null || string.IsNullOrWhiteSpace(j.JobTitle)) continue;

            var locations = (j.PostingLocations ?? new())
                .Select(l => l.FormattedAddress)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct()
                .ToList();

            results.Add(new RawJobPosting
            {
                NativeId = j.JobPostingId.Value.ToString(),
                Title = j.JobTitle!.Trim(),
                Url = $"https://jobs.dayforcehcm.com/{clientNamespace}/{jobBoardCode}/jobs/{j.JobPostingId}",
                Location = locations.Count == 0 ? null : string.Join("; ", locations),
                RemoteType = j.HasVirtualLocation == true ? "remote" : null,
                EmploymentType = null, // not exposed by this endpoint
                Department = null,     // not present on the posting itself, only as a search filter id
                DescriptionSnippet = j.JobDescription,
            });
        }
        return results;
    }

    public sealed class SearchRequest
    {
        [JsonPropertyName("clientNamespace")] public string ClientNamespace { get; set; } = "";
        [JsonPropertyName("jobBoardCode")]     public string JobBoardCode { get; set; } = "";
        [JsonPropertyName("cultureCode")]      public string CultureCode { get; set; } = "en-US";
        [JsonPropertyName("distanceUnit")]     public int DistanceUnit { get; set; } = 0;
        [JsonPropertyName("paginationStart")]  public int PaginationStart { get; set; }
    }

    public sealed class Response
    {
        [JsonPropertyName("jobPostings")] public List<Posting>? JobPostings { get; set; }
        [JsonPropertyName("maxCount")]    public int? MaxCount { get; set; }
        [JsonPropertyName("offset")]      public int? Offset { get; set; }
        [JsonPropertyName("count")]       public int? Count { get; set; }
    }

    public sealed class Posting
    {
        [JsonPropertyName("jobPostingId")]      public int? JobPostingId { get; set; }
        [JsonPropertyName("jobTitle")]          public string? JobTitle { get; set; }
        [JsonPropertyName("jobDescription")]    public string? JobDescription { get; set; }
        [JsonPropertyName("hasVirtualLocation")] public bool? HasVirtualLocation { get; set; }
        [JsonPropertyName("postingLocations")]  public List<PostingLocation>? PostingLocations { get; set; }
    }

    public sealed class PostingLocation
    {
        [JsonPropertyName("formattedAddress")] public string? FormattedAddress { get; set; }
    }
}
