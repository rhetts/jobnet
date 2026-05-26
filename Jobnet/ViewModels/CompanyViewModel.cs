using CommunityToolkit.Mvvm.ComponentModel;
using Jobnet.Data.Repositories;
using Jobnet.Models;

namespace Jobnet.ViewModels;

public partial class CompanyViewModel : ObservableObject
{
    public Company? Company { get; }
    public bool IsAllJobsSentinel { get; }

    [ObservableProperty]
    private int _activeJobCount;

    /// <summary>30-day cohort churn (% of cohort now inactive). Null if the company has no
    /// jobs ≥30 days old yet — shown as "—" in the UI to signal "not enough history".</summary>
    [ObservableProperty]
    private ChurnStat? _churn;

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

    /// <summary>Compact churn label for the sidebar column. Empty for the "All Jobs" sentinel
    /// and any company without a cohort. Format: "12%" — no parentheses, fits in a narrow column.</summary>
    public string ChurnDisplay =>
        IsAllJobsSentinel || Churn is null ? "—"
        : $"{(int)System.Math.Round(Churn.Value.ChurnPct)}%";

    public CompanyViewModel(Company company, int activeJobCount, ChurnStat? churn = null)
    {
        Company = company;
        ActiveJobCount = activeJobCount;
        Churn = churn;
        IsAllJobsSentinel = false;
    }

    private CompanyViewModel(int activeJobCount)
    {
        Company = null;
        IsAllJobsSentinel = true;
        ActiveJobCount = activeJobCount;
    }

    public static CompanyViewModel CreateAllJobsSentinel(int activeJobCount) => new(activeJobCount);

    partial void OnChurnChanged(ChurnStat? value) => OnPropertyChanged(nameof(ChurnDisplay));
}
