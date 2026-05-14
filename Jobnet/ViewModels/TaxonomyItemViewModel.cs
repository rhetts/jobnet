using CommunityToolkit.Mvvm.ComponentModel;

namespace Jobnet.ViewModels;

/// <summary>One row in the Levels or Areas editable list in Settings.</summary>
public partial class TaxonomyItemViewModel : ObservableObject
{
    public int Id { get; }

    [ObservableProperty]
    private string _name;

    public bool IsNew => Id == 0;

    public TaxonomyItemViewModel(int id, string name)
    {
        Id = id;
        _name = name;
    }
}
