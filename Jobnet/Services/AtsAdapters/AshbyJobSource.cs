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

public sealed class AshbyJobSource : IAtsJobSource
{
    public const string Provider = "ats_ashby";
    public string AtsType => "ashby";

    private readonly HttpClient _http;
    private readonly IApiUsageTracker _usage;
    private readonly IRateLimiter _rateLimiter;

    public AshbyJobSource(HttpClient http, IApiUsageTracker usage, IRateLimiter rateLimiter)
    {
        _http = http;
        _usage = usage;
        _rateLimiter = rateLimiter;
    }

    public async Task<IReadOnlyList<RawJobPosting>> FetchAsync(string slug, CancellationToken ct = default)
    {
        var url = $"https://api.ashbyhq.com/posting-api/job-board/{Uri.EscapeDataString(slug)}";

        await _rateLimiter.WaitAsync(Provider, ct);
        _usage.RecordCall(Provider);

        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Ashby HTTP {(int)resp.StatusCode}");
        var payload = await resp.Content.ReadFromJsonAsync<Response>(cancellationToken: ct);
        var items = payload?.Jobs ?? new();
        var results = new List<RawJobPosting>(items.Count);
        foreach (var j in items)
        {
            if (string.IsNullOrEmpty(j.Id) || string.IsNullOrEmpty(j.Title)) continue;
            results.Add(new RawJobPosting
            {
                NativeId = j.Id!,
                Title = j.Title!,
                Url = j.JobUrl ?? j.ApplyUrl,
                Location = j.Location,
                RemoteType = GuessRemoteType(j.IsRemote, j.Location),
                EmploymentType = j.EmploymentType?.ToLowerInvariant(),
                Department = j.Department ?? j.Team,
                DescriptionSnippet = Trunc(j.DescriptionPlain, 500),
            });
        }
        return results;
    }

    private static string GuessRemoteType(bool? isRemote, string? location)
    {
        if (isRemote == true) return "remote";
        if (!string.IsNullOrEmpty(location))
        {
            var l = location.ToLowerInvariant();
            if (l.Contains("remote")) return "remote";
            if (l.Contains("hybrid")) return "hybrid";
        }
        return isRemote == false ? "on-site" : "unknown";
    }

    private static string? Trunc(string? s, int n) => string.IsNullOrEmpty(s) ? null : (s.Length <= n ? s : s.Substring(0, n));

    private sealed class Response
    {
        [JsonPropertyName("jobs")] public List<Posting>? Jobs { get; set; }
    }

    private sealed class Posting
    {
        [JsonPropertyName("id")]               public string? Id { get; set; }
        [JsonPropertyName("title")]            public string? Title { get; set; }
        [JsonPropertyName("jobUrl")]           public string? JobUrl { get; set; }
        [JsonPropertyName("applyUrl")]         public string? ApplyUrl { get; set; }
        [JsonPropertyName("location")]         public string? Location { get; set; }
        [JsonPropertyName("isRemote")]         public bool? IsRemote { get; set; }
        [JsonPropertyName("employmentType")]   public string? EmploymentType { get; set; }
        [JsonPropertyName("department")]       public string? Department { get; set; }
        [JsonPropertyName("team")]             public string? Team { get; set; }
        [JsonPropertyName("descriptionPlain")] public string? DescriptionPlain { get; set; }
    }
}
