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

public sealed class BraveSearchClient : ISearchClient
{
    public const string Provider = "brave_search";
    public string ProviderId => Provider;

    private const string Endpoint = "https://api.search.brave.com/res/v1/web/search";

    private readonly HttpClient _http;
    private readonly IConfigRepository _config;
    private readonly IApiUsageTracker _usage;

    public BraveSearchClient(HttpClient http, IConfigRepository config, IApiUsageTracker usage)
    {
        _http = http;
        _config = config;
        _usage = usage;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, int page = 1, int pageSize = 10, CancellationToken ct = default)
    {
        var apiKey = _config.GetOrDefault("brave_search_api_key", "");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Brave Search is not configured. Set brave_search_api_key in Settings.");

        pageSize = Math.Clamp(pageSize, 1, 20);
        var offset = (page - 1) * pageSize;
        var url = $"{Endpoint}?q={Uri.EscapeDataString(query)}&count={pageSize}&offset={offset}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Subscription-Token", apiKey);
        req.Headers.Add("Accept", "application/json");

        _usage.RecordCall(Provider);
        using var response = await _http.SendAsync(req, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var snippet = Truncate(body, 200);
            var status = (int)response.StatusCode;
            if (status == 400 || status == 401 || status == 403 || status == 422 || status == 429)
                throw new SearchAuthException($"Brave Search HTTP {status} — {snippet}");
            throw new HttpRequestException($"Brave Search request failed: HTTP {status} — {snippet}");
        }

        var payload = await response.Content.ReadFromJsonAsync<BraveResponse>(cancellationToken: ct);
        var items = payload?.Web?.Results ?? new List<BraveResult>();
        var results = new List<SearchResult>(items.Count);
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Url)) continue;
            results.Add(new SearchResult
            {
                Title = item.Title ?? "(no title)",
                Url = item.Url,
                Snippet = item.Description,
                DisplayLink = TryGetHost(item.Url),
            });
        }
        return results;
    }

    private static string? TryGetHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

    private sealed class BraveResponse
    {
        [JsonPropertyName("web")] public BraveWeb? Web { get; set; }
    }

    private sealed class BraveWeb
    {
        [JsonPropertyName("results")] public List<BraveResult>? Results { get; set; }
    }

    private sealed class BraveResult
    {
        [JsonPropertyName("title")]       public string? Title { get; set; }
        [JsonPropertyName("url")]         public string? Url { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
    }
}
