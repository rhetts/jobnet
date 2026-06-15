using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jobnet.Data.Repositories;
using Jobnet.Models;

namespace Jobnet.ViewModels;

/// <summary>
/// Read-only stats dashboard. Refreshes on open and on demand. Pulls everything from repos
/// — no AI calls, no network. Cheap to re-run; the Refresh button just re-queries.
/// </summary>
public partial class StatsViewModel : ObservableObject
{
    private readonly ICompanyRepository _companies;
    private readonly IJobRepository _jobs;
    private readonly IJobProcessingQueueRepository _queue;

    public StatsViewModel(ICompanyRepository companies, IJobRepository jobs, IJobProcessingQueueRepository queue)
    {
        _companies = companies;
        _jobs = jobs;
        _queue = queue;
        Refresh();
    }

    [ObservableProperty] private int _companiesActive;
    [ObservableProperty] private int _companiesInactive;
    [ObservableProperty] private int _companiesTotal;

    [ObservableProperty] private int _jobsActive;
    [ObservableProperty] private int _jobsRemoved;
    [ObservableProperty] private int _jobsTotal;
    [ObservableProperty] private int _jobsWithSummary;
    [ObservableProperty] private int _jobsWithResumeMatch;

    public ObservableCollection<ParserSystemRow> ParserSystems { get; } = new();
    public ObservableCollection<QueueRow> Queue { get; } = new();

    [RelayCommand]
    public void Refresh()
    {
        var all = _companies.GetAll();
        var active = all.Where(c => c.IsActive).ToList();
        CompaniesActive = active.Count;
        CompaniesInactive = all.Count - active.Count;
        CompaniesTotal = all.Count;

        var jobs = _jobs.GetAll(includeRemoved: true);
        JobsActive = jobs.Count(j => j.IsActive);
        JobsRemoved = jobs.Count - JobsActive;
        JobsTotal = jobs.Count;
        JobsWithSummary = jobs.Count(j => j.IsActive && !string.IsNullOrWhiteSpace(j.Summary));
        JobsWithResumeMatch = jobs.Count(j => j.IsActive && j.ResumeMatchScore.HasValue);

        ParserSystems.Clear();
        foreach (var row in BuildParserBreakdown(active))
            ParserSystems.Add(row);

        Queue.Clear();
        foreach (var s in _queue.GetStats())
            Queue.Add(new QueueRow { TaskType = s.TaskType, Status = s.Status, Count = s.Count });
    }

    /// <summary>Group active companies by extraction-system label. Same precedence as
    /// ParserReportViewModel: native ATS (specific) → hand-written → cached selectors →
    /// AI extract → never-scanned. Ordered by company count desc within the report.</summary>
    private static IEnumerable<ParserSystemRow> BuildParserBreakdown(IReadOnlyList<Company> active)
    {
        return active
            .Select(c => Classify(c))
            .GroupBy(s => s)
            .Select(g => new ParserSystemRow { System = g.Key, Count = g.Count() })
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.System)
            .ToList();
    }

    private static string Classify(Company c)
    {
        if (!string.IsNullOrEmpty(c.AtsType) && !string.IsNullOrEmpty(c.AtsSlug))
            return $"native: {c.AtsType}";
        if (!string.IsNullOrWhiteSpace(c.LastCompanyParser))
            return $"hand-written: {c.LastCompanyParser}";
        if (!string.IsNullOrWhiteSpace(c.ParserStrategy) && !c.ParserStrategyDisabled)
            return "cached selectors";
        if (c.DateLastScan is null)
            return "never scanned";
        return "AI extract";
    }
}

public sealed class ParserSystemRow
{
    public required string System { get; init; }
    public required int Count { get; init; }
}

public sealed class QueueRow
{
    public required string TaskType { get; init; }
    public required string Status { get; init; }
    public required int Count { get; init; }
}
