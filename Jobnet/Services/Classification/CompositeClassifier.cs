namespace Jobnet.Services.Classification;

/// <summary>
/// Tries the heuristic first. If it produces no level AND no areas, falls back to the AI client
/// (Gemini or Claude, per the ai_provider config).
/// </summary>
public sealed class CompositeClassifier : IJobClassifier
{
    private readonly HeuristicClassifier _heuristic;
    private readonly AiFallbackClassifier _ai;

    public CompositeClassifier(HeuristicClassifier heuristic, AiFallbackClassifier ai)
    {
        _heuristic = heuristic;
        _ai = ai;
    }

    public ClassificationResult Classify(string title, string? department = null)
    {
        var first = _heuristic.Classify(title, department);
        if (first.LevelId.HasValue || first.Areas.Count > 0) return first;
        return _ai.Classify(title, department);
    }
}
