using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Jobnet.Services.Filters;
using Jobnet.Services.Logging;

namespace Jobnet.ViewModels;

/// <summary>
/// Backs the Scan Times screen. Ranks companies by how much wall-clock the refresher has spent on
/// them (from refresh_attempt.duration_ms) against how many jobs that produced, and offers the two
/// remedies inline: blacklist the company, or block its domain outright in filter_rule.
///
/// The point is to make "which domain is eating the clock?" answerable without hand-written SQL —
/// that question is how Blue Ant Media's 40-minute scans and Hyperwallet's 18-hour hang were
/// found, and both took a manual query to spot.
/// </summary>
public partial class ScanTimesViewModel : ObservableObject
{
    private readonly IRunLogger _runs;
    private readonly ICompanyRepository _companies;
    private readonly IFilterRuleRepository _rules;
    private readonly FilterRuleProvider _filters;

    public ObservableCollection<ScanTimeRowVm> AllRows { get; } = new();
    public ObservableCollection<ScanTimeRowVm> Rows { get; } = new();

    public IReadOnlyList<WindowOption> Windows { get; } = new[]
    {
        new WindowOption("All history", 0),
        new WindowOption("Last 7 days", 7),
        new WindowOption("Last 30 days", 30),
        new WindowOption("Last 90 days", 90),
    };

    public IReadOnlyList<string> Filters { get; } =
        new[] { "(all)", "over 1 hour", "over 10 min", "zero jobs", "not blocked" };

    [ObservableProperty] private WindowOption _selectedWindow;
    [ObservableProperty] private string _selectedFilter = "(all)";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _lastAction = "";

    public ScanTimesViewModel(IRunLogger runs, ICompanyRepository companies,
                               IFilterRuleRepository rules, FilterRuleProvider filters)
    {
        _runs = runs;
        _companies = companies;
        _rules = rules;
        _filters = filters;
        _selectedWindow = Windows[0];
        Refresh();
    }

    [RelayCommand]
    public void Refresh()
    {
        AllRows.Clear();
        foreach (var s in _runs.GetScanTimes(SelectedWindow?.Days ?? 0))
            AllRows.Add(new ScanTimeRowVm(s));
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        var q = AllRows.AsEnumerable();
        q = SelectedFilter switch
        {
            "over 1 hour"  => q.Where(r => r.WorstMs > 3_600_000),
            "over 10 min"  => q.Where(r => r.WorstMs > 600_000),
            "zero jobs"    => q.Where(r => r.JobsYielded == 0),
            "not blocked"  => q.Where(r => !r.IsBlacklisted),
            _              => q,
        };
        var list = q.ToList();

        Rows.Clear();
        foreach (var r in list) Rows.Add(r);

        var totalHours = AllRows.Sum(r => r.TotalMs) / 3_600_000.0;
        var shownHours = list.Sum(r => r.TotalMs) / 3_600_000.0;
        Summary = $"Showing {Rows.Count} of {AllRows.Count} companies  ·  "
                + $"{shownHours:F1} h of {totalHours:F1} h total scan time";
    }

    partial void OnSelectedWindowChanged(WindowOption value) => Refresh();
    partial void OnSelectedFilterChanged(string value) => ApplyFilters();

    /// <summary>Stop refreshing this company. Existing jobs stay put and go stale; nothing is
    /// deleted. Reversible from the Settings → Blacklist tab.</summary>
    [RelayCommand]
    private void ToggleBlacklist(ScanTimeRowVm? row)
    {
        if (row is null) return;
        _companies.SetBlacklisted(row.CompanyId, !row.IsBlacklisted);
        LastAction = row.IsBlacklisted
            ? $"Un-blacklisted {row.Name} — it will be scanned again."
            : $"Blacklisted {row.Name} — it will no longer be scanned.";
        Refresh();
    }

    /// <summary>Add a host rule to filter_rule for this company's domain AND blacklist it. The
    /// rule is what stops discovery re-adding the domain later; the blacklist is what stops the
    /// refresher visiting the row that already exists. One without the other leaves a hole.</summary>
    [RelayCommand]
    private void BlockDomain(ScanTimeRowVm? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.Domain)) return;

        var existing = _rules.GetBySubject(FilterSubject.Host)
            .Any(r => string.Equals(r.Pattern, row.Domain, StringComparison.OrdinalIgnoreCase)
                   && r.Scope is null);

        if (!existing)
        {
            _rules.Insert(new FilterRule
            {
                Subject = FilterSubject.Host,
                Action = FilterAction.Block,
                MatchType = FilterMatchType.Domain,
                Pattern = row.Domain,
                Scope = null,   // everywhere — don't crawl it, don't rediscover it
                Note = $"Blocked from Scan Times: {row.TotalDisplay} spent for {row.JobsYielded} jobs",
                IsEnabled = true,
            });
            _filters.Invalidate();
        }

        _companies.SetBlacklisted(row.CompanyId, true);
        LastAction = existing
            ? $"{row.Domain} was already blocked; blacklisted {row.Name} as well."
            : $"Blocked {row.Domain} and blacklisted {row.Name}. Cached URLs are purged on the next refresh.";
        Refresh();
    }

    public sealed record WindowOption(string Label, int Days)
    {
        public override string ToString() => Label;
    }
}

/// <summary>One row of the Scan Times grid. Built from a snapshot and replaced wholesale on
/// Refresh, matching how ParserReportRow works.</summary>
public sealed class ScanTimeRowVm
{
    public int CompanyId { get; }
    public string Name { get; }
    public string Domain { get; }
    public int Attempts { get; }
    public long TotalMs { get; }
    public long AvgMs { get; }
    public long WorstMs { get; }
    public long CostPerJobMs { get; }
    public int JobsYielded { get; }
    public bool IsBlacklisted { get; }

    public string TotalDisplay { get; }
    public string AvgDisplay { get; }
    public string WorstDisplay { get; }
    public string LastAttemptDisplay { get; }
    public string BlacklistToggleLabel { get; }

    /// <summary>Minutes of scan time per job produced — the column that actually ranks waste.
    /// A company with no jobs shows the raw total rather than dividing by zero.</summary>
    public string CostPerJobDisplay { get; }

    /// <summary>Red over an hour, amber over ten minutes. An hour-plus attempt is a hang, not a
    /// slow site — PlaywrightFetcher caps network idle at 30s, so the wait is further down.</summary>
    public string WorstBrush { get; }

    public ScanTimeRowVm(ScanTimeSummary s)
    {
        CompanyId = s.CompanyId;
        Name = s.Name;
        Domain = s.Domain;
        Attempts = s.Attempts;
        TotalMs = s.TotalMs;
        AvgMs = s.AvgMs;
        WorstMs = s.WorstMs;
        JobsYielded = s.JobsYielded;
        IsBlacklisted = s.IsBlacklisted;

        TotalDisplay = Humanize(s.TotalMs);
        AvgDisplay   = Humanize(s.AvgMs);
        WorstDisplay = Humanize(s.WorstMs);

        CostPerJobMs = s.JobsYielded > 0 ? s.TotalMs / s.JobsYielded : s.TotalMs;
        CostPerJobDisplay = s.JobsYielded > 0
            ? Humanize(CostPerJobMs)
            : $"{Humanize(s.TotalMs)} / 0";

        LastAttemptDisplay = s.LastAttempt is { } t
            ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "—";

        BlacklistToggleLabel = s.IsBlacklisted ? "Un-blacklist" : "Blacklist";

        WorstBrush = s.WorstMs switch
        {
            > 3_600_000 => "#C44",
            >   600_000 => "#E08E0B",
            _           => "#333",
        };
    }

    private static string Humanize(long ms) => ms switch
    {
        >= 3_600_000 => $"{ms / 3_600_000.0:F1} h",
        >=    60_000 => $"{ms / 60_000.0:F1} min",
        >=     1_000 => $"{ms / 1_000.0:F1} s",
        _            => $"{ms} ms",
    };
}
