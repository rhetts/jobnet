using CommunityToolkit.Mvvm.ComponentModel;
using Jobnet.Models;

namespace Jobnet.ViewModels;

public partial class CompanyViewModel : ObservableObject
{
    public Company? Company { get; }
    public bool IsAllJobsSentinel { get; }

    [ObservableProperty]
    private int _activeJobCount;

    public string Name => IsAllJobsSentinel ? "All Jobs" : Company!.Name;

    public InterestLevel InterestLevel => IsAllJobsSentinel ? InterestLevel.Neutral : Company!.InterestLevel;

    public string InterestGlyph => InterestLevel switch
    {
        InterestLevel.Interesting    => "★", // ★
        InterestLevel.NotInteresting => "✗", // ✗
        _                            => " "
    };

    public CompanyViewModel(Company company, int activeJobCount)
    {
        Company = company;
        ActiveJobCount = activeJobCount;
        IsAllJobsSentinel = false;
    }

    private CompanyViewModel(int activeJobCount)
    {
        Company = null;
        IsAllJobsSentinel = true;
        ActiveJobCount = activeJobCount;
    }

    public static CompanyViewModel CreateAllJobsSentinel(int activeJobCount) => new(activeJobCount);
}
