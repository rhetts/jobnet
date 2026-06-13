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
/// SmartRecruiters' public postings API. Paginated:
/// <code>
/// GET https://api.smartrecruiters.com/v1/companies/{slug}/postings?limit=100&amp;offset=0
/// Response: { "totalFound": N, "content": [
///     { "id": "...", "name": "...", "refNumber": "...",
///       "location": { "city": "...", "region": "...", "country": "...",
///                     "remote": bool, "hybrid": bool, "fullLocation": "..." },
///       "department": { "label": "..." },
///       "typeOfEmployment": { "label": "..." },
///       "company": { "identifier": "..." } }, ... ] }
/// </code>
///
/// Slug is the company's SmartRecruiters identifier (URL-safe, mixed case allowed). Job page
/// URLs follow the pattern <c>https://jobs.smartrecruiters.com/{slug}/{id}</c>.
/// </summary>
public sealed class SmartRecruitersJobSource : IJobSource
{
    public const string Provider = "ats_smartrecruiters";
    public string AtsType => "smartrecruiters";

    /// <summary>API cap per page; the docs say 100 max.</summary>
    private const int PageLimit = 100;

    /// <summary>Safety ceiling on pagination. Realistic boards top out around 1–2K postings.</summary>
    private const int MaxPages = 30;

    private readonly HttpClient _http;
    private readonly IApiUsageTracker _usage;
    private readonly IRateLimiter _rateLimiter;

    public SmartRecruitersJobSource(HttpClient http, IApiUsageTracker usage, IRateLimiter rateLimiter)
    {
        _http = http;
        _usage = usage;
        _rateLimiter = rateLimiter;
    }

    public async Task<IReadOnlyList<RawJobPosting>> FetchAsync(string slug, CancellationToken ct = default)
    {
        var all = new List<RawJobPosting>();
        var offset = 0;
        for (var page = 0; page < MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var url = $"https://api.smartrecruiters.com/v1/companies/{Uri.EscapeDataString(slug)}/postings?limit={PageLimit}&offset={offset}";

            await _rateLimiter.WaitAsync(Provider, ct);
            _usage.RecordCall(Provider);

            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"SmartRecruiters HTTP {(int)resp.StatusCode} for slug '{slug}' (page {page + 1})");

            var payload = await resp.Content.ReadFromJsonAsync<Response>(cancellationToken: ct);
            var batch = ParseBatch(payload, slug);
            all.AddRange(batch);

            var total = payload?.TotalFound ?? 0;
            offset += PageLimit;
            if (batch.Count == 0 || offset >= total) break;
        }
        return all;
    }

    /// <summary>Pulled out for unit testing — parse one page given the company slug used to
    /// build job-page URLs.</summary>
    public static IReadOnlyList<RawJobPosting> ParseBatch(Response? payload, string slug)
    {
        var items = payload?.Content ?? new();
        var results = new List<RawJobPosting>(items.Count);
        foreach (var j in items)
        {
            if (string.IsNullOrWhiteSpace(j.Id) || string.IsNullOrWhiteSpace(j.Name)) continue;

            results.Add(new RawJobPosting
            {
                NativeId = j.Id!,
                Title = j.Name!,
                Url = $"https://jobs.smartrecruiters.com/{slug}/{j.Id}",
                Location = j.Location?.FullLocation
                           ?? FormatLocation(j.Location),
                RemoteType = ResolveRemote(j.Location),
                EmploymentType = NormalizeEmployment(j.TypeOfEmployment?.Label),
                Department = j.Department?.Label,
                DescriptionSnippet = null,
            });
        }
        return results;
    }

    private static string? FormatLocation(LocationObj? loc)
    {
        if (loc is null) return null;
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(loc.City))    parts.Add(loc.City!);
        if (!string.IsNullOrWhiteSpace(loc.Region))  parts.Add(loc.Region!);
        if (!string.IsNullOrWhiteSpace(loc.Country)) parts.Add(loc.Country!);
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static string ResolveRemote(LocationObj? loc)
    {
        if (loc is null) return "unknown";
        if (loc.Remote == true) return "remote";
        if (loc.Hybrid == true) return "hybrid";
        return "on-site";
    }

    private static string NormalizeEmployment(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "unknown";
        var l = label.ToLowerInvariant();
        if (l.Contains("full"))                            return "full-time";
        if (l.Contains("part"))                            return "part-time";
        if (l.Contains("contract") || l.Contains("temp"))  return "contract";
        return "unknown";
    }

    public sealed class Response
    {
        [JsonPropertyName("totalFound")] public int? TotalFound { get; set; }
        [JsonPropertyName("content")]    public List<JobPosting>? Content { get; set; }
    }

    public sealed class JobPosting
    {
        [JsonPropertyName("id")]               public string? Id { get; set; }
        [JsonPropertyName("name")]             public string? Name { get; set; }
        [JsonPropertyName("refNumber")]        public string? RefNumber { get; set; }
        [JsonPropertyName("location")]         public LocationObj? Location { get; set; }
        [JsonPropertyName("department")]       public LabelObj? Department { get; set; }
        [JsonPropertyName("typeOfEmployment")] public LabelObj? TypeOfEmployment { get; set; }
    }

    public sealed class LocationObj
    {
        [JsonPropertyName("city")]         public string? City { get; set; }
        [JsonPropertyName("region")]       public string? Region { get; set; }
        [JsonPropertyName("country")]      public string? Country { get; set; }
        [JsonPropertyName("remote")]       public bool? Remote { get; set; }
        [JsonPropertyName("hybrid")]       public bool? Hybrid { get; set; }
        [JsonPropertyName("fullLocation")] public string? FullLocation { get; set; }
    }

    public sealed class LabelObj
    {
        [JsonPropertyName("label")] public string? Label { get; set; }
    }
}
