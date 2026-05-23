namespace Jobnet.ViewModels;

/// <summary>One option in the resume-match filter dropdown on the filter bar.</summary>
public sealed class ResumeMatchThreshold
{
    public string Label { get; }
    public int? MinScore { get; }
    public bool RequireScored { get; }
    public string Key { get; }

    public ResumeMatchThreshold(string label, int? minScore, bool requireScored, string key)
    {
        Label = label;
        MinScore = minScore;
        RequireScored = requireScored;
        Key = key;
    }

    public override string ToString() => Label;
}
