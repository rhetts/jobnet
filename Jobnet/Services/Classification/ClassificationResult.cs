using System.Collections.Generic;

namespace Jobnet.Services.Classification;

public sealed class ClassificationResult
{
    public required int? LevelId { get; init; }
    public required string? LevelName { get; init; }
    public required IReadOnlyList<(int Id, string Name)> Areas { get; init; }
    public required string Source { get; init; }       // 'heuristic', 'claude-haiku', 'none'
    public required string? Reason { get; init; }      // explanation / matched keyword(s)
}
