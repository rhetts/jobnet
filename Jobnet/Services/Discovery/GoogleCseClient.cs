using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Data.Repositories;
using Jobnet.Services.ApiUsage;

namespace Jobnet.Services.Discovery;

public sealed class GoogleCseClient : ISearchClient
{
    public const string Provider = "google_cse";
    public string ProviderId => Provider;

    private const string Endpoint = "https://www.googleapis.com/customsearch/v1";

    private readonly HttpClient _http;
    private readonly IConfigRepository _config;
    private readonly IApiUsageTracker _usage;

    public GoogleCseClient(HttpClient http, IConfigRepository config, IApiUsageTracker usage)
    {
        _http = http;
        _config = config;
        _usage = usage;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, int page = 1, int pageSize = 10, CancellationToken ct = default)
    {
        var apiKey  = _config.GetOrDefault("google_cse_api_key", "");
        var engineId = _config.GetOrDefault("google_cse_engine_id", "");
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(engineId))
            throw new InvalidOperationException(
                "Google CSE is not configured. Set google_cse_api_key and google_cse_engine_id in Settings.");

        pageSize = Math.Clamp(pageSize, 1, 10);
        var start = ((page - 1) * pageSize) + 1; // CSE pagination is 1-based

        var url = $"{Endpoint}?key={Uri.EscapeDataString(apiKey)}" +
                  $"&cx={Uri.EscapeDataString(engineId)}" +
                  $"&q={Uri.EscapeDataString(query)}" +
                  $"&num={pageSize}&start={start}";

        _usage.RecordCall(Provider);
        var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var snippet = ExtractErrorMessage(body) ?? Truncate(body, 200);
            var status = (int)response.StatusCode;
            if (status == 400 || status == 401 || status == 403 || status == 429)
                throw new SearchAuthException($"Google CSE HTTP {status} — {snippet}");
            throw new HttpRequestException($"Google CSE request failed: HTTP {status} — {snippet}");
        }

        var payload = await response.Content.ReadFromJsonAsync<CseResponse>(cancellationToken: ct);
        var items = payload?.Items ?? new List<CseItem>();
        var results = new List<SearchResult>(items.Count);
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Link)) continue;
            results.Add(new SearchResult
            {
                Title = item.Title ?? "(no title)",
                Url = item.Link,
                Snippet = item.Snippet,
                DisplayLink = item.DisplayLink,
            });
        }
        return results;
    }

    private static string? ExtractErrorMessage(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.TryGetProperty("message", out var msg))
                return msg.GetString();
        }
        catch { }
        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

    private sealed class CseResponse
    {
        [JsonPropertyName("items")] public List<CseItem>? Items { get; set; }
    }

    private sealed class CseItem
    {
        [JsonPropertyName("title")]       public string? Title { get; set; }
        [JsonPropertyName("link")]        public string? Link { get; set; }
        [JsonPropertyName("snippet")]     public string? Snippet { get; set; }
        [JsonPropertyName("displayLink")] public string? DisplayLink { get; set; }
    }
}
