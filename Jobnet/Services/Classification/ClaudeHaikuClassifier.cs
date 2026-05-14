using System.Collections.Generic;

namespace Jobnet.Services.Classification;

/// <summary>
/// Stub for the Claude Haiku fallback classifier. Wired in Phase 7 when the Claude CLI integration lands.
/// Until then, returns a "no match" result. Callers fall back to leaving level/areas unset.
/// </summary>
public sealed class ClaudeHaikuClassifier : IJobClassifier
{
    public ClassificationResult Classify(string title, string? department = null) =>
        new()
        {
            LevelId = null,
            LevelName = null,
            Areas = new List<(int, string)>(),
            Source = "none",
            Reason = "Claude Haiku fallback not yet implemented (Phase 7)"
        };
}
