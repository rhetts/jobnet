using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Jobnet.Services;

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
    private readonly IAppPaths _paths;

    /// <summary>Nested VM that powers the "API usage" tab in the merged Stats window. Exposed
    /// publicly so the XAML can DataContext-bind a TabItem's content to it.</summary>
    public ServiceLimitsViewModel ApiUsage { get; }

    /// <summary>Last ~500 lines of jobnet.log, shown on the Log tab. Re-read on every Refresh
    /// so the user always sees the latest. Read with shared file access so the open trace
    /// listener doesn't block us.</summary>
    [ObservableProperty]
    private string _logText = "";

    [ObservableProperty]
    private string _logPath = "";

    public StatsViewModel(ICompanyRepository companies, IJobRepository jobs,
                          IJobProcessingQueueRepository queue, ServiceLimitsViewModel apiUsage,
                          IAppPaths paths)
    {
        _companies = companies;
        _jobs = jobs;
        _queue = queue;
        ApiUsage = apiUsage;
        _paths = paths;
        LogPath = Path.Combine(_paths.DataDirectory, "jobnet.log");
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

    [ObservableProperty] private string _rescoreStatus = "";

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

        // Refresh the API-usage tab in lockstep so a single "Refresh" click updates both views.
        ApiUsage?.Refresh();

        // And reload the log tail. Read with FileShare.ReadWrite | Delete so the running trace
        // listener (which holds the file open for writes) doesn't block us.
        LogText = ReadLogTail(LogPath, maxLines: 500);
    }

    private static string ReadLogTail(string path, int maxLines)
    {
        if (!File.Exists(path)) return "(log file not found at " + path + ")";
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            // Bounded ring buffer — cheaper than slurping the whole 7MB file when we only show
            // the last 500 lines.
            var ring = new string[maxLines];
            var idx = 0; var count = 0;
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                ring[idx] = line;
                idx = (idx + 1) % maxLines;
                if (count < maxLines) count++;
            }
            var sb = new StringBuilder(count * 80);
            var start = count < maxLines ? 0 : idx;
            for (var i = 0; i < count; i++)
                sb.AppendLine(ring[(start + i) % maxLines]);
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"(failed to read log: {ex.GetType().Name}: {ex.Message})";
        }
    }

    /// <summary>Open the data folder in Explorer so the user can grab the full log file.</summary>
    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _paths.DataDirectory,
                UseShellExecute = true
            });
        }
        catch { /* OS will surface its own error if Explorer is broken */ }
    }

    /// <summary>Reset terminal queue rows for unscored active jobs back to 'pending' so the
    /// resume-match worker picks them up again. Catches both 'failed' rows (hit max attempts)
    /// and 'completed' rows whose matcher succeeded but produced no usable score. Active jobs
    /// that already have a score are untouched.</summary>
    [RelayCommand]
    private void RescoreStuckJobs()
    {
        var ids = _jobs.GetActiveIdsMissingResumeMatch();
        if (ids.Count == 0)
        {
            RescoreStatus = "Nothing to do — every active job already has a score.";
            return;
        }
        var reset = _queue.Requeue(ids, JobProcessingTaskTypes.ResumeMatch);
        var newRows = _queue.EnqueueMissing(ids, JobProcessingTaskTypes.ResumeMatch);
        RescoreStatus = $"Requeued {reset} stuck rows, enqueued {newRows} new — {ids.Count} job(s) will be rescored.";
        Refresh();
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
