using CommunityToolkit.Mvvm.ComponentModel;
using Jobnet.Models;

namespace Jobnet.ViewModels;

public partial class AggregatorToggleViewModel : ObservableObject
{
    public AggregatorSource Source { get; }

    [ObservableProperty]
    private bool _isEnabled;

    public string Name => Source.Name;
    public string BaseUrl => Source.BaseUrl;
    public string? Notes => Source.Notes;

    public AggregatorToggleViewModel(AggregatorSource source)
    {
        Source = source;
        IsEnabled = source.IsEnabled;
    }
}
