using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Services.ApiUsage;
using Jobnet.Services.RateLimit;

namespace Jobnet.Services.AtsAdapters;

public sealed class GreenhouseJobSource : IAtsJobSource
{
    public const string Provider = "ats_greenhouse";
    public string AtsType => "greenhouse";

    private readonly HttpClient _http;
    private readonly IApiUsageTracker _usage;
    private readonly IRateLimiter _rateLimiter;

    public GreenhouseJobSource(HttpClient http, IApiUsageTracker usage, IRateLimiter rateLimiter)
    {
        _http = http;
        _usage = usage;
        _rateLimiter = rateLimiter;
    }

    public async Task<IReadOnlyList<RawJobPosting>> FetchAsync(string slug, CancellationToken ct = default)
    {
        var url = $"https://boards-api.greenhouse.io/v1/boards/{Uri.EscapeDataString(slug)}/jobs?content=true";

        await _rateLimiter.WaitAsync(Provider, ct);
        _usage.RecordCall(Provider);

        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Greenhouse HTTP {(int)resp.StatusCode}");
        var payload = await resp.Content.ReadFromJsonAsync<Response>(cancellationToken: ct);
        var items = payload?.Jobs ?? new();
        var results = new List<RawJobPosting>(items.Count);
        foreach (var j in items)
        {
            if (j.Id is null || string.IsNullOrEmpty(j.Title)) continue;
            results.Add(new RawJobPosting
            {
                NativeId = j.Id.Value.ToString(),
                Title = j.Title!,
                Url = j.AbsoluteUrl,
                Location = j.Location?.Name,
                RemoteType = GuessRemoteType(j.Location?.Name),
                EmploymentType = "full-time",
                Department = (j.Departments != null && j.Departments.Count > 0) ? j.Departments[0].Name : null,
                DescriptionSnippet = StripTags(j.Content ?? "")?[..System.Math.Min(500, (j.Content ?? "").Length)],
            });
        }
        return results;
    }

    private static string? StripTags(string html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        var s = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        s = System.Net.WebUtility.HtmlDecode(s);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    private static string GuessRemoteType(string? location)
    {
        if (string.IsNullOrEmpty(location)) return "unknown";
        var l = location.ToLowerInvariant();
        if (l.Contains("remote")) return "remote";
        if (l.Contains("hybrid")) return "hybrid";
        return "on-site";
    }

    private sealed class Response
    {
        [JsonPropertyName("jobs")] public List<Posting>? Jobs { get; set; }
    }

    private sealed class Posting
    {
        [JsonPropertyName("id")]            public long? Id { get; set; }
        [JsonPropertyName("title")]         public string? Title { get; set; }
        [JsonPropertyName("absolute_url")]  public string? AbsoluteUrl { get; set; }
        [JsonPropertyName("location")]      public LocationObj? Location { get; set; }
        [JsonPropertyName("departments")]   public List<NamedObj>? Departments { get; set; }
        [JsonPropertyName("content")]       public string? Content { get; set; }
    }

    private sealed class LocationObj
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private sealed class NamedObj
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }
}
