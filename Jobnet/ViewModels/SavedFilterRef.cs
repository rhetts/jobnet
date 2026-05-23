namespace Jobnet.ViewModels;

/// <summary>Lightweight reference used in the saved-filters dropdown — only the
/// fields the ComboBox displays / passes back on selection.</summary>
public sealed class SavedFilterRef
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Payload { get; init; } = string.Empty;
    public override string ToString() => Name;
}
