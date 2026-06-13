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

public sealed class ClaudeClient : IAiClient
{
    public const string Provider = "claude_haiku";
    public string ProviderId => Provider;

    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly IConfigRepository _config;
    private readonly IApiUsageTracker _usage;
    private readonly IRateLimiter _rateLimiter;
    private readonly IApiQuotaController _quota;

    public ClaudeClient(HttpClient http, IConfigRepository config, IApiUsageTracker usage,
                         IRateLimiter rateLimiter, IApiQuotaController quota)
    {
        _http = http;
        _config = config;
        _usage = usage;
        _rateLimiter = rateLimiter;
        _quota = quota;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config.GetOrDefault("claude_api_key", ""));

    public async Task<AiResponse> CompleteAsync(string userMessage, string? system = null, int? maxTokens = null, CancellationToken ct = default, string? task = null)
    {
        _ = task;
        var apiKey = _config.GetOrDefault("claude_api_key", "");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new AiUnavailableException("Claude API key is not configured. Set claude_api_key in Settings.");

        var model = _config.GetOrDefault("claude_model", "claude-haiku-4-5");
        var cap = maxTokens ?? int.Parse(_config.GetOrDefault("claude_max_tokens_classify", "256"));

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = cap,
            ["messages"] = new[] { new { role = "user", content = userMessage } }
        };
        if (!string.IsNullOrEmpty(system)) payload["system"] = system;

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(payload)
        };
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", ApiVersion);

        await _rateLimiter.WaitAsync(Provider, ct);
        _usage.RecordCall(Provider);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(req, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Network-level failure (DNS, TLS, timeout): no HTTP status, but log the attempt so
            // the post-mortem CLI can tell "call started but never reached Anthropic" apart from
            // "call never started".
            _usage.RecordCallOutcome(Provider, 0, $"{ex.GetType().Name}: {ex.Message}");
            throw;
        }

        using var _disposeResponse = response;
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var snapshot = _usage.GetSnapshot(Provider);
            var isDaily = body.Contains("per_day", StringComparison.OrdinalIgnoreCase)
                       || body.Contains("daily", StringComparison.OrdinalIgnoreCase)
                       || (snapshot.RpdCap > 0 && snapshot.Rpd >= snapshot.RpdCap);
            var label = isDaily ? "daily" : "per-minute";
            _usage.RecordCallOutcome(Provider, 429, $"({label}) {Truncate(body, 400)}");
            throw new AiUnavailableException($"Claude API HTTP 429 ({label}) — {Truncate(body, 300)}");
        }
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var msg = ExtractErrorMessage(body) ?? Truncate(body, 300);
            _usage.RecordCallOutcome(Provider, (int)response.StatusCode, Truncate(body, 400));
            throw new AiUnavailableException($"Claude API HTTP {(int)response.StatusCode} — {msg}");
        }

        var parsed = await response.Content.ReadFromJsonAsync<ApiResponse>(cancellationToken: ct)
                     ?? throw new AiUnavailableException("Claude API returned empty response body.");
        var text = parsed.Content?.Count > 0 ? string.Concat(parsed.Content.ConvertAll(c => c.Text ?? "")) : "";

        var inTok  = parsed.Usage?.InputTokens ?? 0;
        var outTok = parsed.Usage?.OutputTokens ?? 0;
        _usage.UpdateLastCallTokens(Provider, inTok, outTok);
        _usage.RecordCallOutcome(Provider, 200);

        return new AiResponse
        {
            Text = text,
            InputTokens = inTok,
            OutputTokens = outTok,
            Model = parsed.Model ?? model,
            ProviderId = Provider,
        };
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
        [JsonPropertyName("content")] public List<ContentBlock>? Content { get; set; }
        [JsonPropertyName("usage")]   public TokenUsage? Usage { get; set; }
        [JsonPropertyName("model")]   public string? Model { get; set; }
    }

    private sealed class ContentBlock
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
    }

    private sealed class TokenUsage
    {
        [JsonPropertyName("input_tokens")]  public int InputTokens  { get; set; }
        [JsonPropertyName("output_tokens")] public int OutputTokens { get; set; }
    }
}
