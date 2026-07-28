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

    /// <summary>Total jobs we've ever tracked for this company (active + removed). Renders next
    /// to ActiveJobCount in the sidebar so the user can tell a long-history company with
    /// high churn (e.g. "2 / 47") from a brand-new one ("3 / 3").</summary>
    [ObservableProperty]
    private int _totalJobCount;

    /// <summary>30-day cohort churn (% of cohort now inactive). Null if the company has no
    /// jobs ≥30 days old yet — shown as "—" in the UI to signal "not enough history".</summary>
    [ObservableProperty]
    private ChurnStat? _churn;

    public string Name => IsAllJobsSentinel ? "All Jobs" : Company!.Name;

    public string? City => IsAllJobsSentinel ? null : Company?.City;
    public bool HasCity => !string.IsNullOrWhiteSpace(City);

    /// <summary>True for recruitment agencies, used to render an amber chip in the sidebar
    /// and on job cards so the user can spot agency postings at a glance.</summary>
    public bool IsAgency => !IsAllJobsSentinel && (Company?.IsAgency ?? false);

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

    /// <summary>Sidebar job-count label. Shows "active / total" when there's any history,
    /// otherwise just the active count. Examples: "3" (only-ever-active), "3 / 47" (active
    /// out of historical), "0 / 12" (company we've tracked but nothing's currently posted).</summary>
    public string JobCountDisplay
    {
        get
        {
            if (TotalJobCount <= 0 || TotalJobCount == ActiveJobCount)
                return ActiveJobCount.ToString();
            return $"{ActiveJobCount} / {TotalJobCount}";
        }
    }

    public CompanyViewModel(Company company, int activeJobCount, int totalJobCount, ChurnStat? churn = null)
    {
        Company = company;
        ActiveJobCount = activeJobCount;
        TotalJobCount = totalJobCount;
        Churn = churn;
        IsAllJobsSentinel = false;
    }

    private CompanyViewModel(int activeJobCount, int totalJobCount)
    {
        Company = null;
        IsAllJobsSentinel = true;
        ActiveJobCount = activeJobCount;
        TotalJobCount = totalJobCount;
    }

    public static CompanyViewModel CreateAllJobsSentinel(int activeJobCount, int totalJobCount) =>
        new(activeJobCount, totalJobCount);

    partial void OnChurnChanged(ChurnStat? value) => OnPropertyChanged(nameof(ChurnDisplay));
    partial void OnActiveJobCountChanged(int value) => OnPropertyChanged(nameof(JobCountDisplay));
    partial void OnTotalJobCountChanged(int value)  => OnPropertyChanged(nameof(JobCountDisplay));
}
