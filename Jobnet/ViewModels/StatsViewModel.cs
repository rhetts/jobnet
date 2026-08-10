using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
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

    /// <summary>Chart geometry for the Postings tab — kept in sync with the Canvas size in
    /// StatsWindow.xaml (640x160). Computed here rather than in a converter since the VM
    /// already exposes raw brush/display strings elsewhere in this app (see WorstBrush in
    /// ScanTimeRowVm) — same pragmatic pattern, just for chart points instead of colors.</summary>
    private const double PostingChartWidth = 640;
    private const double PostingChartHeight = 160;

    [ObservableProperty] private PointCollection _postingHistoryPoints = new();
    [ObservableProperty] private string _postingHistoryMaxLabel = "";
    [ObservableProperty] private string _postingHistoryStartLabel = "";
    [ObservableProperty] private string _postingHistoryEndLabel = "";
    [ObservableProperty] private string _postingHistoryCurrentLabel = "";

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

        BuildPostingHistory();

        Queue.Clear();
        foreach (var s in _queue.GetStats())
            Queue.Add(new QueueRow { TaskType = s.TaskType, Status = s.Status, Count = s.Count });

        // Refresh the API-usage tab in lockstep so a single "Refresh" click updates both views.
        ApiUsage?.Refresh();

        // And reload the log tail. Read with FileShare.ReadWrite | Delete so the running trace
        // listener (which holds the file open for writes) doesn't block us.
        LogText = ReadLogTail(LogPath, maxLines: 500);
    }

    /// <summary>Builds a daily active-postings-count series from date_first_seen/date_removed —
    /// the only history we actually record — and projects it into chart-space points. A day
    /// with no scan just carries the previous count forward, since nothing in the data changed
    /// that day; this is the best available estimate, not a claim a scan ran daily.</summary>
    private void BuildPostingHistory()
    {
        var history = _jobs.GetPostingHistory();
        if (history.Count == 0)
        {
            PostingHistoryPoints = new PointCollection();
            PostingHistoryMaxLabel = PostingHistoryStartLabel = PostingHistoryEndLabel = PostingHistoryCurrentLabel = "";
            return;
        }

        var startDay = history.Min(h => h.FirstSeen.Date);
        var endDay = DateTime.UtcNow.Date;
        var dayCount = (endDay - startDay).Days + 1;

        var counts = new int[dayCount];
        for (var i = 0; i < dayCount; i++)
        {
            var day = startDay.AddDays(i);
            counts[i] = history.Count(h => h.FirstSeen.Date <= day && (h.Removed is null || h.Removed.Value.Date > day));
        }

        var maxCount = Math.Max(1, counts.Max());
        var points = new PointCollection();
        for (var i = 0; i < dayCount; i++)
        {
            var x = dayCount == 1 ? 0 : i * PostingChartWidth / (dayCount - 1);
            var y = PostingChartHeight - counts[i] / (double)maxCount * PostingChartHeight;
            points.Add(new Point(x, y));
        }

        PostingHistoryPoints = points;
        PostingHistoryMaxLabel = maxCount.ToString();
        PostingHistoryStartLabel = startDay.ToString("MMM d");
        PostingHistoryEndLabel = endDay.ToString("MMM d");
        PostingHistoryCurrentLabel = $"{counts[^1]} active as of {endDay:MMM d}";
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
