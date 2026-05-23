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

    public string? City => IsAllJobsSentinel ? null : Company?.City;
    public bool HasCity => !string.IsNullOrWhiteSpace(City);

    public InterestLevel InterestLevel => IsAllJobsSentinel ? InterestLevel.Neutral : Company!.InterestLevel;

    public string InterestGlyph => InterestLevel switch
    {
        InterestLevel.Approved    => "★", // ★
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
