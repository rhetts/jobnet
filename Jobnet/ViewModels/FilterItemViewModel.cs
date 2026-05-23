using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Jobnet.ViewModels;

public partial class FilterItemViewModel : ObservableObject
{
    public int Id { get; }
    public string Name { get; }
    private readonly Action _onChanged;

    [ObservableProperty]
    private bool _isSelected;

    public FilterItemViewModel(int id, string name, bool initialSelected, Action onChanged)
    {
        Id = id;
        Name = name;
        _isSelected = initialSelected;
        _onChanged = onChanged;
    }

    partial void OnIsSelectedChanged(bool value) => _onChanged();
}
