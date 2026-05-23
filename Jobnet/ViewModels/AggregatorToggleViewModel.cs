using CommunityToolkit.Mvvm.ComponentModel;
using Jobnet.Models;

namespace Jobnet.ViewModels;

/// <summary>Editable row in the Settings → Boards tab. Wraps an aggregator_sources record
/// (or a brand-new one with Id=0 when IsNew is true).</summary>
public partial class AggregatorToggleViewModel : ObservableObject
{
    public int Id { get; }
    public bool IsNew { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _baseUrl = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private int _maxPages = 1;

    public AggregatorToggleViewModel(AggregatorSource source)
    {
        Id = source.Id;
        IsNew = source.Id == 0;
        _name = source.Name ?? "";
        _baseUrl = source.BaseUrl ?? "";
        _notes = source.Notes ?? "";
        _isEnabled = source.IsEnabled;
        _maxPages = source.MaxPages <= 0 ? 1 : source.MaxPages;
    }
}
