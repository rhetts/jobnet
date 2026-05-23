using CommunityToolkit.Mvvm.ComponentModel;

namespace Jobnet.ViewModels;

public partial class DiscoverySeedItemViewModel : ObservableObject
{
    public int Id { get; }
    public bool IsNew { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private int _maxPages = 1;

    public DiscoverySeedItemViewModel(int id, string name, string url, string? description, bool isEnabled, int maxPages = 1)
    {
        Id = id;
        IsNew = id == 0;
        _name = name;
        _url = url;
        _description = description ?? "";
        _isEnabled = isEnabled;
        _maxPages = maxPages <= 0 ? 1 : maxPages;
    }
}
