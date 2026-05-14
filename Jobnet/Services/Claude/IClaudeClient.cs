using System.Threading;
using System.Threading.Tasks;

namespace Jobnet.Services.Claude;

public interface IClaudeClient
{
    bool IsConfigured { get; }

    /// <summary>Send a single user message and get a text response. Throws ClaudeUnavailableException if not configured.</summary>
    Task<ClaudeResponse> CompleteAsync(string userMessage, string? system = null, int? maxTokens = null, CancellationToken ct = default);
}

public sealed class ClaudeResponse
{
    public required string Text { get; init; }
    public required int InputTokens { get; init; }
    public required int OutputTokens { get; init; }
    public required string Model { get; init; }
}

public sealed class ClaudeUnavailableException : System.Exception
{
    public ClaudeUnavailableException(string message) : base(message) { }
}
