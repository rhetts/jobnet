using System;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Data.Repositories;

namespace Jobnet.Services.Ai;

/// <summary>Picks the concrete AI client based on the `ai_provider` config value at call time.</summary>
public sealed class RoutingAiClient : IAiClient
{
    private readonly GeminiClient _gemini;
    private readonly ClaudeClient _claude;
    private readonly IConfigRepository _config;

    public RoutingAiClient(GeminiClient gemini, ClaudeClient claude, IConfigRepository config)
    {
        _gemini = gemini;
        _claude = claude;
        _config = config;
    }

    public string ProviderId => Resolve().ProviderId;
    public bool IsConfigured => Resolve().IsConfigured;

    public Task<AiResponse> CompleteAsync(string userMessage, string? system = null, int? maxTokens = null, CancellationToken ct = default) =>
        Resolve().CompleteAsync(userMessage, system, maxTokens, ct);

    private IAiClient Resolve()
    {
        var which = _config.GetOrDefault("ai_provider", "gemini").ToLowerInvariant();
        return which switch
        {
            "gemini" or "google"           => _gemini,
            "claude" or "anthropic"        => _claude,
            _ => throw new InvalidOperationException(
                $"Unknown ai_provider '{which}'. Set to 'gemini' or 'claude' in Settings.")
        };
    }
}
