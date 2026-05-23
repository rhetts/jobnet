namespace Jobnet.ViewModels;

/// <summary>One option in the "posted within" filter dropdown — restrict the jobs view to those
/// first seen within the last N days. Null Days means "any age" (no date restriction).</summary>
public sealed class JobAgeFilter
{
    public string Label { get; }
    public int? Days { get; }
    public string Key { get; }

    public JobAgeFilter(string label, int? days, string key)
    {
        Label = label;
        Days = days;
        Key = key;
    }

    public override string ToString() => Label;
}
