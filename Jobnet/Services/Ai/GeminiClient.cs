using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Data.Repositories;
using Jobnet.Services.ApiUsage;
using Jobnet.Services.RateLimit;

namespace Jobnet.Services.Ai;

/// <summary>
/// Google Gemini client. Uses the AI Studio (generativelanguage) REST endpoint with a plain API key —
/// no service account or Vertex setup needed. Free tier available (typically 10-15 RPM, ~1000 RPD).
/// </summary>
public sealed class GeminiClient : IAiClient
{
    public const string Provider = "gemini";
    public string ProviderId => Provider;

    private const string EndpointBase = "https://generativelanguage.googleapis.com/v1beta/models";

    private readonly HttpClient _http;
    private readonly IConfigRepository _config;
    private readonly IApiUsageTracker _usage;
    private readonly IRateLimiter _rateLimiter;

    public GeminiClient(HttpClient http, IConfigRepository config, IApiUsageTracker usage, IRateLimiter rateLimiter)
    {
        _http = http;
        _config = config;
        _usage = usage;
        _rateLimiter = rateLimiter;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config.GetOrDefault("gemini_api_key", ""));

    public async Task<AiResponse> CompleteAsync(string userMessage, string? system = null, int? maxTokens = null, CancellationToken ct = default)
    {
        var apiKey = _config.GetOrDefault("gemini_api_key", "");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new AiUnavailableException("Gemini API key is not configured. Set gemini_api_key in Settings (get a free key at https://aistudio.google.com/apikey).");

        var model = _config.GetOrDefault("gemini_model", "gemini-2.5-flash-lite");
        var cap = maxTokens ?? int.Parse(_config.GetOrDefault("gemini_max_tokens_classify", "256"));

        var payload = new Dictionary<string, object?>
        {
            ["contents"] = new[] { new { role = "user", parts = new[] { new { text = userMessage } } } },
            ["generationConfig"] = new { maxOutputTokens = cap, temperature = 0.0 }
        };
        if (!string.IsNullOrEmpty(system))
            payload["systemInstruction"] = new { parts = new[] { new { text = system } } };

        var url = $"{EndpointBase}/{Uri.EscapeDataString(model)}:generateContent?key={Uri.EscapeDataString(apiKey)}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload)
        };

        await _rateLimiter.WaitAsync(Provider, ct);
        _usage.RecordCall(Provider);

        var response = await _http.SendAsync(req, ct);

        // 429 retry: Google's error body says "Please retry in Xs". Honor it (capped at 90s).
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var waitSec = ExtractRetryAfter(body);
            response.Dispose();
            if (waitSec > 0 && waitSec <= 90)
            {
                await Task.Delay(TimeSpan.FromSeconds(waitSec + 1), ct);
                _usage.RecordCall(Provider);
                using var retryReq = BuildRequest(url, payload);
                response = await _http.SendAsync(retryReq, ct);
            }
            else
            {
                throw new AiUnavailableException(
                    $"Gemini API HTTP 429 — quota exceeded and retry-after ({waitSec}s) is too large. {Truncate(body, 200)}");
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            using var _ = response;
            var body = await response.Content.ReadAsStringAsync(ct);
            var msg = ExtractErrorMessage(body) ?? Truncate(body, 300);
            throw new AiUnavailableException($"Gemini API HTTP {(int)response.StatusCode} — {msg}");
        }
        using var _ok = response;

        var parsed = await response.Content.ReadFromJsonAsync<ApiResponse>(cancellationToken: ct)
                     ?? throw new AiUnavailableException("Gemini API returned empty response body.");

        string text = "";
        if (parsed.Candidates is { Count: > 0 } &&
            parsed.Candidates[0].Content is { Parts: { Count: > 0 } } content)
        {
            foreach (var p in content.Parts!) text += p.Text ?? "";
        }

        return new AiResponse
        {
            Text = text,
            InputTokens = parsed.UsageMetadata?.PromptTokenCount ?? 0,
            OutputTokens = parsed.UsageMetadata?.CandidatesTokenCount ?? 0,
            Model = parsed.ModelVersion ?? model,
            ProviderId = Provider,
        };
    }

    private static HttpRequestMessage BuildRequest(string url, System.Collections.Generic.Dictionary<string, object?> payload)
    {
        return new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
    }

    /// <summary>Parse Google's 429 body for "Please retry in X.Ys". Returns seconds (rounded up) or 0 if not found.</summary>
    private static int ExtractRetryAfter(string body)
    {
        var m = System.Text.RegularExpressions.Regex.Match(body, @"retry in (\d+\.?\d*)s", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return 0;
        if (!double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec)) return 0;
        return (int)Math.Ceiling(sec);
    }

    private static string? ExtractErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.TryGetProperty("message", out var msg))
                return msg.GetString();
        }
        catch { }
        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

    private sealed class ApiResponse
    {
        [JsonPropertyName("candidates")]    public List<Candidate>? Candidates { get; set; }
        [JsonPropertyName("usageMetadata")] public Usage? UsageMetadata { get; set; }
        [JsonPropertyName("modelVersion")]  public string? ModelVersion { get; set; }
    }

    private sealed class Candidate
    {
        [JsonPropertyName("content")] public Content? Content { get; set; }
    }

    private sealed class Content
    {
        [JsonPropertyName("parts")] public List<Part>? Parts { get; set; }
    }

    private sealed class Part
    {
        [JsonPropertyName("text")] public string? Text { get; set; }
    }

    private sealed class Usage
    {
        [JsonPropertyName("promptTokenCount")]     public int PromptTokenCount { get; set; }
        [JsonPropertyName("candidatesTokenCount")] public int CandidatesTokenCount { get; set; }
        [JsonPropertyName("totalTokenCount")]      public int TotalTokenCount { get; set; }
    }
}
