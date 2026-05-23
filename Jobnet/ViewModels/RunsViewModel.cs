using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jobnet.Services.Logging;

namespace Jobnet.ViewModels;

public partial class RunsViewModel : ObservableObject
{
    private readonly IRunLogger _runs;

    public ObservableCollection<RunRow> AllRuns { get; } = new();
    public ObservableCollection<RunRow> Runs { get; } = new();
    public ObservableCollection<StepRow> Steps { get; } = new();

    public ObservableCollection<string> RunTypes { get; } = new();
    public ObservableCollection<string> StatusOptions { get; } = new() { "(any)", "completed", "partial", "failed", "running", "cancelled" };

    [ObservableProperty] private string _selectedRunType = "(any)";
    [ObservableProperty] private string _selectedStatus = "(any)";
    [ObservableProperty] private int _limit = 100;

    [ObservableProperty] private RunRow? _selectedRun;

    [ObservableProperty] private string _summary = "";

    /// <summary>Shown in the Steps pane when the selected run produced no step rows (or no run
    /// is selected). Keeps the pane from looking broken.</summary>
    [ObservableProperty] private string _stepsEmptyMessage = "Select a run on the left.";

    public RunsViewModel(IRunLogger runs)
    {
        _runs = runs;
        Refresh();
    }

    [RelayCommand]
    public void Refresh()
    {
        AllRuns.Clear();
        foreach (var r in _runs.GetRecent(500))
            AllRuns.Add(new RunRow(r));

        RunTypes.Clear();
        RunTypes.Add("(any)");
        foreach (var t in AllRuns.Select(r => r.RunType).Distinct().OrderBy(t => t))
            RunTypes.Add(t);

        ApplyFilters();
    }

    private void ApplyFilters()
    {
        var q = AllRuns.AsEnumerable();
        if (SelectedRunType != "(any)") q = q.Where(r => r.RunType == SelectedRunType);
        if (SelectedStatus != "(any)") q = q.Where(r => r.Status == SelectedStatus);
        var list = q.Take(Limit).ToList();

        Runs.Clear();
        foreach (var r in list) Runs.Add(r);
        Summary = $"{Runs.Count} run(s) shown · {AllRuns.Count} total recorded";
        if (SelectedRun is not null && !Runs.Contains(SelectedRun)) SelectedRun = null;
    }

    partial void OnSelectedRunTypeChanged(string value) => ApplyFilters();
    partial void OnSelectedStatusChanged(string value)  => ApplyFilters();
    partial void OnLimitChanged(int value)              => ApplyFilters();

    partial void OnSelectedRunChanged(RunRow? value)
    {
        Steps.Clear();
        if (value is null)
        {
            StepsEmptyMessage = "Select a run on the left.";
            return;
        }
        foreach (var s in _runs.GetSteps(value.Id))
            Steps.Add(new StepRow(s));
        StepsEmptyMessage = Steps.Count == 0
            ? $"This run did not record per-step details. The top-level counts on the left are the full breakdown."
            : "";
    }
}

public sealed class RunRow
{
    public long Id { get; }
    public string RunType { get; }
    public string? Scope { get; }
    public string Status { get; }
    public string StartedDisplay { get; }
    public string DurationDisplay { get; }
    public int Examined { get; }
    public int Added { get; }
    public int Updated { get; }
    public int Skipped { get; }
    public int Failed { get; }
    public int ErrorCount { get; }
    public string? Notes { get; }
    public string StatusBrush { get; }

    public RunRow(RunSummary s)
    {
        Id = s.Id;
        RunType = s.RunType;
        Scope = s.Scope;
        Status = s.Status;
        StartedDisplay = s.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        DurationDisplay = s.DurationMs.HasValue ? FormatMs(s.DurationMs.Value) : (s.Status == "running" ? "…" : "—");
        Examined = s.Examined; Added = s.Added; Updated = s.Updated; Skipped = s.Skipped; Failed = s.Failed;
        ErrorCount = s.ErrorCount;
        Notes = s.Notes;
        StatusBrush = s.Status switch
        {
            "completed" => "#2A8F4F",
            "partial"   => "#E08E0B",
            "failed"    => "#C44",
            "cancelled" => "#888",
            _           => "#1976D2",
        };
    }

    private static string FormatMs(int ms) =>
        ms < 1000 ? $"{ms}ms" : ms < 60_000 ? $"{ms / 1000.0:0.0}s" : $"{ms / 60_000.0:0.0}m";
}

public sealed class StepRow
{
    public string StepName { get; }
    public string Status { get; }
    public string DurationDisplay { get; }
    public int Examined { get; }
    public int Added { get; }
    public int Updated { get; }
    public int Skipped { get; }
    public int Failed { get; }
    public string? ErrorMessage { get; }
    public string StatusBrush { get; }

    public StepRow(StepSummary s)
    {
        StepName = s.StepName;
        Status = s.Status;
        DurationDisplay = s.DurationMs.HasValue ? FormatMs(s.DurationMs.Value) : (s.Status == "running" ? "…" : "—");
        Examined = s.Examined; Added = s.Added; Updated = s.Updated; Skipped = s.Skipped; Failed = s.Failed;
        ErrorMessage = s.ErrorMessage;
        StatusBrush = s.Status switch
        {
            "completed" => "#2A8F4F",
            "partial"   => "#E08E0B",
            "failed"    => "#C44",
            _           => "#888",
        };
    }

    private static string FormatMs(int ms) =>
        ms < 1000 ? $"{ms}ms" : ms < 60_000 ? $"{ms / 1000.0:0.0}s" : $"{ms / 60_000.0:0.0}m";
}
