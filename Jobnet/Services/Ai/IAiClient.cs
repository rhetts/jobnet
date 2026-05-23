using System.Threading;
using System.Threading.Tasks;

namespace Jobnet.Services.Ai;

/// <summary>Provider-agnostic chat-completion interface. Implementations: Claude, Gemini.</summary>
public interface IAiClient
{
    /// <summary>Friendly identifier for this provider (matches the api_usage / rate_limit key).</summary>
    string ProviderId { get; }

    /// <summary>True when the provider has the credentials it needs to make a call.</summary>
    bool IsConfigured { get; }

    /// <summary>Send a user message and get a text response. Throws AiUnavailableException if creds are missing.
    /// <paramref name="task"/> is an optional caller-supplied tag (e.g. "extraction", "cover_letter") that
    /// RoutingAiClient uses to pick a per-task provider chain from <c>ai_provider.{task}</c> config keys.
    /// Underlying clients (Gemini/Groq/Claude/LLama) ignore it.</summary>
    Task<AiResponse> CompleteAsync(string userMessage, string? system = null, int? maxTokens = null, CancellationToken ct = default, string? task = null);
}

public sealed class AiResponse
{
    public required string Text { get; init; }
    public required int InputTokens { get; init; }
    public required int OutputTokens { get; init; }
    public required string Model { get; init; }
    public required string ProviderId { get; init; }
}

public sealed class AiUnavailableException : System.Exception
{
    public AiUnavailableException(string message) : base(message) { }
}
