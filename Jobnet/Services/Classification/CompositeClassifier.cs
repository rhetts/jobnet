namespace Jobnet.Services.Classification;

/// <summary>
/// Tries the heuristic first. If it produces no level AND no areas, falls back to Claude Haiku.
/// </summary>
public sealed class CompositeClassifier : IJobClassifier
{
    private readonly HeuristicClassifier _heuristic;
    private readonly ClaudeHaikuClassifier _claude;

    public CompositeClassifier(HeuristicClassifier heuristic, ClaudeHaikuClassifier claude)
    {
        _heuristic = heuristic;
        _claude = claude;
    }

    public ClassificationResult Classify(string title, string? department = null)
    {
        var first = _heuristic.Classify(title, department);
        if (first.LevelId.HasValue || first.Areas.Count > 0) return first;
        return _claude.Classify(title, department);
    }
}
