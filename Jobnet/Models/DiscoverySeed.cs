using System;

namespace Jobnet.Models;

public sealed class DiscoverySeed
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string Url { get; init; }
    public string? Description { get; init; }
    public bool IsEnabled { get; init; } = true;
    public int SortOrder { get; init; }
    public int MaxPages { get; init; } = 1;
    public DateTime DateAdded { get; init; }

    /// <summary>Name of the hand-written <c>IDirectoryPatternParser</c> registered for this
    /// source, if any. Null means no custom parser has ever matched this URL — every harvest
    /// goes through AI extraction.</summary>
    public string? CustomParserName { get; init; }

    /// <summary>'ok' or 'error' from the most recent harvest that had a matching custom parser.
    /// Null when no custom parser is registered, or none has run yet.</summary>
    public string? CustomParserLastResult { get; init; }

    /// <summary>Exception message from the last custom-parser failure. Cleared on the next
    /// successful parse. Surfaced on the Parser Report screen so a broken directory parser
    /// doesn't fail silently into the AI fallback with nothing visible in the UI.</summary>
    public string? CustomParserLastError { get; init; }

    public DateTime? CustomParserLastResultAt { get; init; }
}
