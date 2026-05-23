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

public sealed class GroqClient : IAiClient
{
    public const string Provider = "groq";
    public string ProviderId => Provider;

    private const string Endpoint = "https://api.groq.com/openai/v1/chat/completions";

    private readonly HttpClient _http;
    private readonly IConfigRepository _config;
    private readonly IApiUsageTracker _usage;
    private readonly IRateLimiter _rateLimiter;

    public GroqClient(HttpClient http, IConfigRepository config, IApiUsageTracker usage, IRateLimiter rateLimiter)
    {
        _http = http;
        _config = config;
        _usage = usage;
        _rateLimiter = rateLimiter;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config.GetOrDefault("groq_api_key", ""));

    public async Task<AiResponse> CompleteAsync(string userMessage, string? system = null, int? maxTokens = null, CancellationToken ct = default, string? task = null)
    {
        _ = task; // routing concern only; the leaf client doesn't care.
        var apiKey = _config.GetOrDefault("groq_api_key", "");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new AiUnavailableException("Groq API key is not configured. Set groq_api_key in Settings (free key at https://console.groq.com).");

        var model = _config.GetOrDefault("groq_model", "llama-3.3-70b-versatile");
        var cap = maxTokens ?? int.Parse(_config.GetOrDefault("groq_max_tokens", "1024"));

        var messages = new List<object>();
        if (!string.IsNullOrEmpty(system)) messages.Add(new { role = "system", content = system });
        messages.Add(new { role = "user", content = userMessage });

        var payload = new Dictionary<string, object?>
        {
            ["model"]       = model,
            ["messages"]    = messages,
            ["max_tokens"]  = cap,
            ["temperature"] = 0.0,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(payload)
        };
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");

        await _rateLimiter.WaitAsync(Provider, ct);
        _usage.RecordCall(Provider);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(req, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Network-level failure (DNS, TLS, timeout): no HTTP status to record.
            _usage.RecordCallOutcome(Provider, 0, $"{ex.GetType().Name}: {ex.Message}");
            throw;
        }

        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                var snapshot = _usage.GetSnapshot(Provider);
                var isDaily = body.Contains("rate_limit_exceeded", StringComparison.OrdinalIgnoreCase)
                           && (body.Contains("per day", StringComparison.OrdinalIgnoreCase)
                               || body.Contains("daily", StringComparison.OrdinalIgnoreCase)
                               || (snapshot.RpdCap > 0 && snapshot.Rpd >= snapshot.RpdCap));
                _usage.RecordCallOutcome(Provider, 429, $"({(isDaily ? "daily" : "per-minute")}) {Truncate(body, 400)}");
                CaptureRateLimitHeaders(response);
                throw new AiUnavailableException($"Groq HTTP 429 ({(isDaily ? "daily" : "per-minute")}) — {Truncate(body, 300)}");
            }
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _usage.RecordCallOutcome(Provider, (int)response.StatusCode, Truncate(body, 400));
                throw new AiUnavailableException($"Groq HTTP {(int)response.StatusCode} — {Truncate(body, 300)}");
            }

            var parsed = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: ct)
                         ?? throw new AiUnavailableException("Groq returned empty response body.");
            var text = parsed.Choices?.Count > 0 ? parsed.Choices[0].Message?.Content ?? "" : "";

            var inTok  = parsed.Usage?.PromptTokens     ?? 0;
            var outTok = parsed.Usage?.CompletionTokens ?? 0;
            _usage.UpdateLastCallTokens(Provider, inTok, outTok);
            _usage.RecordCallOutcome(Provider, 200);
            CaptureRateLimitHeaders(response);

            return new AiResponse
            {
                Text = text,
                InputTokens = inTok,
                OutputTokens = outTok,
                Model = parsed.Model ?? model,
                ProviderId = Provider,
            };
        }
    }

    /// <summary>Groq follows the OpenAI rate-limit header convention. There is no dedicated
    /// /usage endpoint, so the only way to read current limits is to scrape these headers off
    /// any response (success OR 429). We persist them into config keys so the Limits screen and
    /// future diagnostics can show them without making an extra call.</summary>
    private void CaptureRateLimitHeaders(HttpResponseMessage response)
    {
        TryStore(response, "x-ratelimit-limit-requests",     "groq_rl_limit_requests");
        TryStore(response, "x-ratelimit-remaining-requests", "groq_rl_remaining_requests");
        TryStore(response, "x-ratelimit-limit-tokens",       "groq_rl_limit_tokens");
        TryStore(response, "x-ratelimit-remaining-tokens",   "groq_rl_remaining_tokens");
        TryStore(response, "x-ratelimit-reset-requests",     "groq_rl_reset_requests");
        TryStore(response, "x-ratelimit-reset-tokens",       "groq_rl_reset_tokens");
        _config.Set("groq_rl_last_seen", DateTime.UtcNow.ToString("o"));
    }

    private void TryStore(HttpResponseMessage response, string header, string configKey)
    {
        if (response.Headers.TryGetValues(header, out var values))
        {
            var v = string.Join(",", values);
            if (!string.IsNullOrWhiteSpace(v)) _config.Set(configKey, v);
        }
    }

    private static string Truncate(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "...");

    private sealed class ChatResponse
    {
        [JsonPropertyName("model")]   public string? Model { get; set; }
        [JsonPropertyName("choices")] public List<Choice>? Choices { get; set; }
        [JsonPropertyName("usage")]   public TokUsage? Usage { get; set; }
    }
    private sealed class Choice
    {
        [JsonPropertyName("message")] public Msg? Message { get; set; }
    }
    private sealed class Msg
    {
        [JsonPropertyName("content")] public string? Content { get; set; }
    }
    private sealed class TokUsage
    {
        [JsonPropertyName("prompt_tokens")]     public int? PromptTokens { get; set; }
        [JsonPropertyName("completion_tokens")] public int? CompletionTokens { get; set; }
    }
}
