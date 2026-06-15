using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jobnet.Data.Repositories;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Jobnet.Services.JobSources;
using Jobnet.Services.Classification;
using Jobnet.Services.Discovery;
using Jobnet.Services.Discovery.Strategies;
using Jobnet.Services.Location;
using Jobnet.Services.Logging;
using Jobnet.Services.Resume;
using Jobnet.Services.Summarization;

namespace Jobnet.ViewModels;

public partial class RefreshViewModel : ObservableObject
{
    private readonly IDiscoveryService _discovery;
    private readonly ICompanyDirectoryHarvester _harvester;
    private readonly IDiscoveryStrategyProvider _strategyProvider;

    [ObservableProperty]
    private bool _useBraveSearch;

    [ObservableProperty]
    private bool _useDirectories = true;   // primary high-yield source — on by default

    [ObservableProperty]
    private bool _useBoards;

    [ObservableProperty]
    private string _lastBraveRunText = "";

    [ObservableProperty]
    private string _lastDirectoriesRunText = "";

    [ObservableProperty]
    private string _lastBoardsRunText = "";

    [ObservableProperty]
    private string _lastDiscoverJobsRunText = "";

    [ObservableProperty]
    private string _lastRefreshExistingRunText = "";

    // LastCompanyProfilesRunText removed — profile generation is continuous via the worker.

    [ObservableProperty]
    private string _lastReclassifyRunText = "";

    /// <summary>Compact one-line summary of which AI provider chain each task is using right now.
    /// Shown beneath the Status text in the refresh window so the user can see at a glance whether
    /// extraction / derivation are about to hit Gemini, the local Llama, etc.</summary>
    [ObservableProperty]
    private string _activeAiRouting = "";

    /// <summary>Per-session counter of AI calls grouped by provider. Updated live as RecordCall
    /// events fire on the IApiUsageTracker. Resets at the start of each run via BeginSession.</summary>
    [ObservableProperty]
    private string _aiCallCounter = "AI calls — none yet.";

    /// <summary>Per-provider call counts for the current session. Mutated from the
    /// IApiUsageTracker.CallRecorded handler; reset in BeginSession. Locked for safety because
    /// the handler fires on whatever thread RecordCall was called from (often a worker).</summary>
    private readonly System.Collections.Generic.Dictionary<string, int> _sessionAiCalls =
        new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>Set of provider IDs we count as "AI" (vs. the misc http_fetch / playwright_fetch /
    /// ats_* providers also recorded by the tracker). Kept in sync with the AI clients.</summary>
    private static readonly System.Collections.Generic.HashSet<string> AiProviders =
        new(System.StringComparer.OrdinalIgnoreCase) { "gemini", "claude", "groq", "llama" };

    [ObservableProperty]
    private string _lastPruneRunText = "";

    public sealed record SkipDaysOption(string Label, int Days);
    public System.Collections.Generic.IReadOnlyList<SkipDaysOption> SkipDaysOptions { get; } = new SkipDaysOption[]
    {
        new("Crawl all pages",           0),
        new("Skip if crawled today",     1),
        new("Skip if crawled in 3 days", 3),
        new("Skip if crawled in 7 days", 7),
    };

    [ObservableProperty]
    private SkipDaysOption? _selectedSkipDays;

    public sealed record SkipScanOption(string Label, int Days);
    public System.Collections.Generic.IReadOnlyList<SkipScanOption> SkipScanOptions { get; } = new SkipScanOption[]
    {
        new("Refresh all companies",       0),
        new("Skip if scanned today",       1),
        new("Skip if scanned in 3 days",   3),
        new("Skip if scanned in 7 days",   7),
        new("Skip if scanned in 14 days", 14),
    };

    [ObservableProperty]
    private SkipScanOption? _selectedSkipScan;

    // Maintenance checkboxes — each step in the consolidated batch. Persisted per-step so the
    // user's last selection survives reopens.
    [ObservableProperty] private bool _doDiscoverJobs = true;
    [ObservableProperty] private bool _doRefreshExisting;
    [ObservableProperty] private bool _doRegenerateSummaries;
    [ObservableProperty] private bool _doReclassifyJobs = true;
    [ObservableProperty] private bool _doPruneOutOfArea = true;

    private readonly IConfigRepository _configRepo;
    private readonly ICompanyRepository _companiesRepo;
    private readonly Jobnet.Services.Profiling.ICompanyProfiler _profiler;
    private readonly IJobRefresher _refresher;
    private readonly IJobDetailRefresher _detailRefresher;
    private readonly IJobSummarizer _summarizer;
    private readonly IJobReclassifier _reclassifier;
    private readonly IJobRepository _jobs;
    private readonly IResumeMatcher _resume;
    private readonly IRunLogger _runs;
    private readonly Jobnet.Services.ApiUsage.IApiQuotaController _quota;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DiscoverCompaniesCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscoverJobsCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshExistingCommand))]
    [NotifyCanExecuteChangedFor(nameof(PruneOutOfAreaCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReclassifyJobsCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopRunCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunMaintenanceCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "Pick an action.";

    /// <summary>Raised after any operation completes so the main window can reload its data.</summary>
    public event Action? Completed;

    public RefreshViewModel(IDiscoveryService discovery, ICompanyDirectoryHarvester harvester,
                             IDiscoveryStrategyProvider strategyProvider,
                             IJobRefresher refresher,
                             IJobDetailRefresher detailRefresher, IJobSummarizer summarizer,
                             IJobReclassifier reclassifier, IJobRepository jobs,
                             IResumeMatcher resume,
                             IRunLogger runs,
                             IConfigRepository config,
                             ICompanyRepository companies,
                             Jobnet.Services.Profiling.ICompanyProfiler profiler,
                             Jobnet.Services.ApiUsage.IApiQuotaController quota,
                             Jobnet.Services.ApiUsage.IApiUsageTracker usageTracker)
    {
        _discovery = discovery;
        _harvester = harvester;
        _strategyProvider = strategyProvider;
        _refresher = refresher;
        _detailRefresher = detailRefresher;
        _summarizer = summarizer;
        _reclassifier = reclassifier;
        _jobs = jobs;
        _resume = resume;
        _runs = runs;
        _configRepo = config;
        _companiesRepo = companies;
        _profiler = profiler;
        _quota = quota;

        // Subscribe to live API-call events so we can render a per-provider counter on the
        // refresh screen. The tracker fires on the recording thread (worker); marshal to the
        // UI dispatcher before mutating the observable property.
        usageTracker.CallRecorded += (_, e) =>
        {
            if (!AiProviders.Contains(e.Provider)) return;
            void Apply()
            {
                lock (_sessionAiCalls)
                {
                    _sessionAiCalls.TryGetValue(e.Provider, out var n);
                    _sessionAiCalls[e.Provider] = n + 1;
                    RebuildAiCallCounter();
                }
            }
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) Apply();
            else dispatcher.BeginInvoke(Apply);
        };

        RefreshAiRouting();

        // Restore previously-selected skip-days; default to "Crawl all pages" (0d).
        var saved = int.TryParse(_configRepo.GetOrDefault("discovery_skip_days_threshold", "0"), out var sd) ? sd : 0;
        _selectedSkipDays = SkipDaysOptions.FirstOrDefault(o => o.Days == saved) ?? SkipDaysOptions[0];

        var savedScan = int.TryParse(_configRepo.GetOrDefault("discover_jobs_skip_days_threshold", "0"), out var ss) ? ss : 0;
        _selectedSkipScan = SkipScanOptions.FirstOrDefault(o => o.Days == savedScan) ?? SkipScanOptions[0];

        _doDiscoverJobs        = _configRepo.GetOrDefault("ui_maint_discover_jobs",        "true")  == "true";
        _doRefreshExisting     = _configRepo.GetOrDefault("ui_maint_refresh_existing",     "false") == "true";
        _doRegenerateSummaries = _configRepo.GetOrDefault("ui_maint_regen_summaries",      "false") == "true";
        _doReclassifyJobs      = _configRepo.GetOrDefault("ui_maint_reclassify",           "true")  == "true";
        _doPruneOutOfArea      = _configRepo.GetOrDefault("ui_maint_prune",                "true")  == "true";

        RefreshLastRunTimes();
    }


    private void RefreshLastRunTimes()
    {
        LastBraveRunText       = FormatLastRun(_runs.GetLastRunStartedAt("discover_companies", "brave"));
        LastDirectoriesRunText = FormatLastRun(_runs.GetLastRunStartedAt("discover_companies", "directories"));
        LastBoardsRunText      = FormatLastRun(_runs.GetLastRunStartedAt("discover_companies", "boards"));

        LastDiscoverJobsRunText         = FormatLastRun(_runs.GetLastRunStartedAt("refresh_jobs"));
        LastRefreshExistingRunText      = FormatLastRun(_runs.GetLastRunStartedAt("refresh_existing"));
        // summary + resume-match no longer have a "last run" timestamp — they're continuous
        // background workers now, not one-shot user-triggered runs.
        // company-profile backfill is now continuous via CompanyProfileWorker — no "last run" timestamp.
        LastReclassifyRunText           = FormatLastRun(_runs.GetLastRunStartedAt("reclassify_jobs"));
        LastPruneRunText                = FormatLastRun(_runs.GetLastRunStartedAt("prune_out_of_area"));
    }

    private static string FormatLastRun(DateTime? whenUtc)
    {
        if (whenUtc is null) return "never run";
        var ago = DateTime.UtcNow - whenUtc.Value;
        string rel;
        if (ago.TotalSeconds < 60)     rel = "just now";
        else if (ago.TotalMinutes < 60) rel = $"{(int)ago.TotalMinutes}m ago";
        else if (ago.TotalHours < 24)   rel = $"{(int)ago.TotalHours}h ago";
        else if (ago.TotalDays < 30)    rel = $"{(int)ago.TotalDays}d ago";
        else                            rel = whenUtc.Value.ToLocalTime().ToString("yyyy-MM-dd");
        return $"last run {rel}";
    }

    partial void OnIsBusyChanged(bool value)
    {
        // Whenever a command finishes, repopulate the per-action "last run" labels.
        if (!value) RefreshLastRunTimes();
    }

    partial void OnSelectedSkipDaysChanged(SkipDaysOption? value)
    {
        _configRepo?.Set("discovery_skip_days_threshold", (value?.Days ?? 0).ToString());
    }

    partial void OnSelectedSkipScanChanged(SkipScanOption? value)
    {
        _configRepo?.Set("discover_jobs_skip_days_threshold", (value?.Days ?? 0).ToString());
    }

    private bool CanRun() => !IsBusy;

    private bool CanRunDiscover() => !IsBusy && (UseBraveSearch || UseDirectories || UseBoards);

    private bool CanStop() => IsBusy;

    /// <summary>Stop any in-flight batch by firing the session cancellation token. The currently
    /// executing AI call (especially local llama) does NOT abort mid-token — it finishes, the
    /// next ct check in the outer loop sees the cancellation, and the run unwinds with status
    /// 'cancelled'. Expect ~1 in-flight company to still write its results before stopping.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void StopRun()
    {
        StatusText = "Stop requested — waiting for current step to finish...";
        _quota.CancelSession();
    }

    /// <summary>Build a Progress&lt;T&gt; that mirrors batch-service ticks into run_step_log rows. The
    /// "starting" stage opens a step; "done" closes it with the per-item counts. Captured on the
    /// UI thread so the SQLite writes run on the dispatcher (single-writer; SQLite WAL is fine).</summary>
    private IProgress<Jobnet.Services.Logging.BatchStepProgress> StepProgressFor(long runId)
    {
        long currentStepId = 0;
        return new Progress<Jobnet.Services.Logging.BatchStepProgress>(p =>
        {
            if (p.Stage == "starting")
            {
                currentStepId = _runs.StartStep(runId, p.Name);
            }
            else if (currentStepId > 0)
            {
                _runs.FinishStep(currentStepId,
                    status: p.Status ?? "completed",
                    added: p.Added, updated: p.Updated, skipped: p.Skipped, failed: p.Failed,
                    errorMessage: p.ErrorMessage);
                currentStepId = 0;
            }
        });
    }

    /// <summary>Begin a new batch session: clears any prior per-day cancel decisions and
    /// returns a fresh CancellationToken that fires if the user clicks "No" on a quota popup.
    /// During consolidated maintenance, the session has already been established by the runner
    /// — return the existing token instead of resetting it mid-chain (which would clobber any
    /// pending cancel signal).</summary>
    private System.Threading.CancellationToken BeginSession()
    {
        if (!_runningMaintenance) _quota.ResetSession();
        // Re-read the routing chain so a Settings change between runs shows up in the status bar.
        RefreshAiRouting();
        // Zero the per-session AI counter at the start of each new run (maintenance batches keep
        // their counter rolling across the contained steps — _runningMaintenance gates that).
        if (!_runningMaintenance)
        {
            lock (_sessionAiCalls) { _sessionAiCalls.Clear(); RebuildAiCallCounter(); }
        }
        return _quota.SessionCancellationToken;
    }

    /// <summary>Recompute the AI-call counter display string from <see cref="_sessionAiCalls"/>.
    /// Caller must hold the lock.</summary>
    private void RebuildAiCallCounter()
    {
        if (_sessionAiCalls.Count == 0)
        {
            AiCallCounter = "AI calls — none yet.";
            return;
        }
        var total = 0;
        foreach (var v in _sessionAiCalls.Values) total += v;
        var parts = _sessionAiCalls.OrderByDescending(kv => kv.Value)
                                    .Select(kv => $"{kv.Key} {kv.Value}");
        AiCallCounter = $"AI calls — total {total}  ·  {string.Join("  ·  ", parts)}";
    }

    /// <summary>Status text to show when a step finishes via OperationCanceledException. The
    /// daily-quota message is only correct when a cloud provider actually triggered the quota
    /// dialog — for local llama (which never produces a quota event) the cancel came from the
    /// user's Stop button, an inference timeout, or some other CT trigger.</summary>
    private string CancelledStatusText() =>
        _quota.WasCancelledByDailyQuota
            ? "Cancelled — daily quota reached."
            : "Cancelled.";

    /// <summary>True when at least one maintenance checkbox is selected.</summary>
    private bool CanRunMaintenance() => !IsBusy && (DoDiscoverJobs || DoRefreshExisting || DoRegenerateSummaries || DoReclassifyJobs || DoPruneOutOfArea);

    /// <summary>Recompute the "Routing — extraction: X · selector_derive: Y" line from current
    /// config. Called from the constructor and at the start of each run so a Settings change
    /// shows up the next time the user kicks off work.</summary>
    public void RefreshAiRouting()
    {
        var global = _configRepo.GetOrDefault("ai_provider", "gemini").Trim();
        var extraction = ResolveTaskChain("extraction", global);
        var derive = ResolveTaskChain("selector_derive", global);
        ActiveAiRouting = $"Routing — extraction: {extraction}   ·   selector_derive: {derive}";
    }

    /// <summary>Lookup helper that mirrors <c>RoutingAiClient.ResolveChain</c>: per-task override
    /// wins; an empty / unset override falls through to the global chain. Returns a display string
    /// like "gemini → llama" so the user sees the actual fallback order, not just the head.</summary>
    private string ResolveTaskChain(string task, string globalChain)
    {
        var raw = _configRepo.GetOrDefault($"ai_provider.{task}", "").Trim();
        if (string.IsNullOrEmpty(raw)) raw = globalChain;
        if (raw is "both" or "auto") raw = "gemini,groq";
        var parts = raw.Replace("+", ",").Split(',', System.StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "(unset)" : string.Join(" → ", parts.Select(p => p.Trim()));
    }

    partial void OnDoDiscoverJobsChanged(bool value)
    {
        _configRepo?.Set("ui_maint_discover_jobs", value ? "true" : "false");
        RunMaintenanceCommand?.NotifyCanExecuteChanged();
    }
    partial void OnDoRefreshExistingChanged(bool value)
    {
        _configRepo?.Set("ui_maint_refresh_existing", value ? "true" : "false");
        RunMaintenanceCommand?.NotifyCanExecuteChanged();
    }
    partial void OnDoRegenerateSummariesChanged(bool value)
    {
        _configRepo?.Set("ui_maint_regen_summaries", value ? "true" : "false");
        RunMaintenanceCommand?.NotifyCanExecuteChanged();
    }
    partial void OnDoReclassifyJobsChanged(bool value)
    {
        _configRepo?.Set("ui_maint_reclassify", value ? "true" : "false");
        RunMaintenanceCommand?.NotifyCanExecuteChanged();
    }
    partial void OnDoPruneOutOfAreaChanged(bool value)
    {
        _configRepo?.Set("ui_maint_prune", value ? "true" : "false");
        RunMaintenanceCommand?.NotifyCanExecuteChanged();
    }

    /// <summary>Run every checked maintenance step in order. Each step manages its own run_log row;
    /// the consolidated runner just chains the calls and stops on cancellation. IsBusy is held for
    /// the whole chain so individual steps can't double-trigger via their own CanRun guard.</summary>
    [RelayCommand(CanExecute = nameof(CanRunMaintenance))]
    private async Task RunMaintenanceAsync()
    {
        _runningMaintenance = true;
        IsBusy = true;
        var ct = BeginSession();
        try
        {
            if (DoDiscoverJobs        && !ct.IsCancellationRequested) await DiscoverJobsAsync();
            if (DoRefreshExisting     && !ct.IsCancellationRequested) await RefreshExistingAsync();
            // Summary regenerate-all removed — summaries are generated by SummaryWorker as
            // new jobs land. DoRegenerateSummaries toggle is now a no-op kept for binding
            // compatibility until the maintenance UI is refactored.
            // ReclassifyJobs and PruneOutOfArea are sync and they (a) mutate ObservableCollections
            // via Completed?.Invoke → LoadFromDataService and (b) run in ~tens of milliseconds
            // even on hundreds of jobs. Wrapping them in Task.Run pushes the collection mutations
            // to a worker thread and trips WPF's CollectionView dispatcher check. Just call them
            // synchronously on the UI thread — the brief block is harmless at this size.
            if (DoReclassifyJobs      && !ct.IsCancellationRequested) ReclassifyJobs();
            if (DoPruneOutOfArea      && !ct.IsCancellationRequested) PruneOutOfArea();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Maintenance cancelled.";
        }
        finally
        {
            _runningMaintenance = false;
            IsBusy = false;
        }
    }

    /// <summary>When true, the per-step IsBusy=false in finally blocks is suppressed so the spinner
    /// stays on across chained steps. Set inside RunMaintenanceAsync only.</summary>
    private bool _runningMaintenance;

    [RelayCommand(CanExecute = nameof(CanRunDiscover))]
    private async Task DiscoverCompaniesAsync()
    {
        var sources = new List<IDiscoveryStrategy>();
        if (UseBraveSearch) sources.Add(_strategyProvider.GetWebSearch());
        if (UseDirectories) sources.AddRange(_strategyProvider.GetDirectoryStrategies());
        if (UseBoards)      sources.AddRange(_strategyProvider.GetBoardStrategies());

        if (sources.Count == 0) { StatusText = "Pick at least one source."; return; }

        IsBusy = true;
        var ct = BeginSession();
        var summary = new StrategyReport();
        var scope = string.Join("+", new[]
        {
            UseBraveSearch ? "brave" : null,
            UseDirectories ? "directories" : null,
            UseBoards      ? "boards"      : null,
        }.Where(x => x != null));
        var runId = _runs.StartRun("discover_companies", scope);
        var runStatus = "completed";
        try
        {
            foreach (var (s, i) in sources.Select((s, i) => (s, i)))
            {
                if (ct.IsCancellationRequested) { runStatus = "cancelled"; break; }
                StatusText = $"[{i + 1}/{sources.Count}] {s.Name}...";
                var stepId = _runs.StartStep(runId, s.Name);
                try
                {
                    var r = await Task.Run(() => s.RunAsync(), ct).ConfigureAwait(true);
                    summary.CandidatesExamined       += r.CandidatesExamined;
                    summary.CompaniesAdded           += r.CompaniesAdded;
                    summary.CompaniesSkippedExisting += r.CompaniesSkippedExisting;
                    summary.CompaniesSkippedFiltered += r.CompaniesSkippedFiltered;
                    foreach (var e in r.Errors) summary.Errors.Add($"[{s.Name}] {e}");

                    _runs.FinishStep(stepId,
                        status: r.Errors.Count == 0 ? "completed" : "partial",
                        examined: r.CandidatesExamined,
                        added:    r.CompaniesAdded,
                        skipped:  r.CompaniesSkippedExisting,
                        failed:   r.CompaniesSkippedFiltered,
                        errorMessage: r.Errors.Count == 0 ? null : string.Join(" | ", r.Errors.Take(3)));
                    if (r.Errors.Count > 0) runStatus = "partial";
                }
                catch (OperationCanceledException)
                {
                    _runs.FinishStep(stepId, status: "cancelled");
                    runStatus = "cancelled";
                    break;
                }
                catch (Exception ex)
                {
                    summary.Errors.Add($"[{s.Name}] {ex.GetType().Name}: {ex.Message}");
                    _runs.FinishStep(stepId, status: "failed", errorMessage: $"{ex.GetType().Name}: {ex.Message}");
                    runStatus = "partial";
                }
            }

            var prefix = runStatus == "cancelled" ? "Cancelled — " : "Done: ";
            StatusText = $"{prefix}{sources.Count} source(s), " +
                         $"{summary.CandidatesExamined} examined, " +
                         $"{summary.CompaniesAdded} added, " +
                         $"{summary.CompaniesSkippedExisting} already known, " +
                         $"{summary.CompaniesSkippedFiltered} filtered."
                         + (summary.Errors.Count > 0 ? $"  First error: {summary.Errors[0]}" : "");
            Completed?.Invoke();

            // Auto-profile newly-added companies (capped). Skipped if the user cancelled the run.
            if (runStatus != "cancelled" && summary.CompaniesAdded > 0)
            {
                var autoProfile = _configRepo.GetOrDefault("auto_profile_on_discovery", "true") == "true";
                if (autoProfile)
                {
                    var profiled = await Task.Run(() => ProfileMissing(max: 30, ct), ct).ConfigureAwait(true);
                    if (profiled.profiled > 0 || profiled.failed > 0)
                        StatusText += $"  Profiles: {profiled.profiled} generated, {profiled.failed} failed.";
                    Completed?.Invoke();
                }
            }
        }
        catch (OperationCanceledException)
        {
            runStatus = "cancelled";
            StatusText = CancelledStatusText();
        }
        catch (Exception ex)
        {
            runStatus = "failed";
            StatusText = $"Failed: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            _runs.FinishRun(runId, status: runStatus,
                examined: summary.CandidatesExamined,
                added: summary.CompaniesAdded,
                skipped: summary.CompaniesSkippedExisting,
                failed: summary.CompaniesSkippedFiltered,
                errorCount: summary.Errors.Count,
                notes: summary.Errors.Count == 0 ? null : string.Join(" | ", summary.Errors.Take(5)));
            IsBusy = false;
        }
    }

    /// <summary>Generate AI profiles for companies that don't have one. Synchronous (caller should
    /// wrap in Task.Run if used from the UI thread). Returns counts.</summary>
    private (int profiled, int failed) ProfileMissing(int max, System.Threading.CancellationToken ct)
    {
        var pending = _companiesRepo.GetAll()
            .Where(c => _companiesRepo.GetProfile(c.Id) is null)
            .Take(max)
            .ToList();
        var ok = 0; var fail = 0;
        foreach (var co in pending)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var r = _profiler.GenerateAndPersistAsync(co, ct).GetAwaiter().GetResult();
                if (r.Success) ok++; else fail++;
            }
            catch { fail++; }
        }
        return (ok, fail);
    }

    // GenerateCompanyProfilesAsync removed — profile generation is now continuous via
    // CompanyProfileWorker, which polls job_processing_queue rows of task_type='company_profile'.
    // ICompanyProfiler is still injected (for one-off `profile-company` CLI use); the worker
    // calls GenerateAndPersistAsync per dequeued row.

    partial void OnUseBraveSearchChanged(bool value) => DiscoverCompaniesCommand.NotifyCanExecuteChanged();
    partial void OnUseDirectoriesChanged(bool value) => DiscoverCompaniesCommand.NotifyCanExecuteChanged();
    partial void OnUseBoardsChanged(bool value)      => DiscoverCompaniesCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task DiscoverJobsAsync()
    {
        IsBusy = true;
        var ct = BeginSession();
        var skipDays = SelectedSkipScan?.Days ?? 0;
        var scope = skipDays > 0 ? $"skip<{skipDays}d" : "all";
        StatusText = skipDays > 0
            ? $"Pulling jobs from companies not scanned in the last {skipDays} day(s)..."
            : "Pulling jobs from known companies (ATS endpoints + careers pages)...";
        var runId = _runs.StartRun("refresh_jobs", scope);
        // Track running totals so that if the user cancels (or an exception escapes) we can
        // still record what was accomplished before unwinding. Declared OUTSIDE the try so the
        // catch blocks can read them — without this, every cancelled refresh writes 0/0/0/0
        // to run_log and the Run History page looks like nothing happened even when dozens of
        // companies were already processed.
        var prevAdded = 0;
        var prevUpdated = 0;
        var prevErrors = 0;
        var examinedSoFar = 0;
        try
        {
            // Progress<T> captures the current SynchronizationContext (the UI dispatcher), so the
            // callback runs on the UI thread automatically — safe to mutate StatusText directly.
            // Also emit one run_step_log row per company so the Runs window's Steps pane is useful
            // (per-company breakdown of what added/updated/errored). Deltas come from the SoFar
            // cumulative counters: prev values are captured in this closure.
            long currentStepId = 0;
            var progress = new Progress<Jobnet.Services.JobSources.JobRefreshProgress>(p =>
            {
                var verb = p.Stage == "starting" ? "scanning" : "done";
                StatusText = $"[{p.Current}/{p.Total}] {verb} {p.CompanyName} ({p.CompanyDomain}) — "
                           + $"{p.JobsAddedSoFar} added, {p.JobsUpdatedSoFar} updated"
                           + (p.ErrorsSoFar > 0 ? $", {p.ErrorsSoFar} errors" : "");

                if (p.Stage == "starting")
                {
                    currentStepId = _runs.StartStep(runId, $"{p.CompanyName} ({p.CompanyDomain})");
                }
                else if (currentStepId > 0)
                {
                    var addedDelta   = p.JobsAddedSoFar   - prevAdded;
                    var updatedDelta = p.JobsUpdatedSoFar - prevUpdated;
                    var errorsDelta  = p.ErrorsSoFar      - prevErrors;
                    prevAdded   = p.JobsAddedSoFar;
                    prevUpdated = p.JobsUpdatedSoFar;
                    prevErrors  = p.ErrorsSoFar;
                    examinedSoFar++;

                    var status = errorsDelta > 0 ? "partial" : "completed";
                    // Pass the refresher's per-company classification through so `runs show <id>`
                    // surfaces *which kind* of failure each company had — not just status=partial.
                    _runs.FinishStep(currentStepId,
                        status: status,
                        added: addedDelta, updated: updatedDelta, failed: errorsDelta,
                        errorMessage: p.ErrorMessage,
                        outcomeKind: p.OutcomeKind);
                    currentStepId = 0;
                }
            });
            var r = await Task.Run(() => _refresher.RefreshAllAsync(minDaysSinceLastScan: skipDays, progress: progress, ct: ct, runId: runId), ct).ConfigureAwait(true);
            StatusText = $"Refresh: {r.CompaniesProcessed} companies, " +
                         (r.CompaniesSkippedRecent > 0 ? $"{r.CompaniesSkippedRecent} skipped (recent), " : "") +
                         $"{r.JobsAdded} added, {r.JobsUpdated} updated, {r.JobsRemoved} marked removed."
                         + (r.Errors.Count > 0 ? $"  First error: {r.Errors[0]}" : "");
            _runs.FinishRun(runId, r.Errors.Count == 0 ? "completed" : "partial",
                examined: r.CompaniesProcessed, added: r.JobsAdded, updated: r.JobsUpdated,
                skipped: r.CompaniesSkippedRecent,
                failed: r.JobsRemoved, errorCount: r.Errors.Count,
                notes: r.Errors.Count == 0 ? null : string.Join(" | ", r.Errors.Take(5)));
            Completed?.Invoke();
        }
        catch (OperationCanceledException)
        {
            StatusText = CancelledStatusText();
            // Preserve partial progress on cancel — the cumulative counters tracked through the
            // progress callback are the best snapshot we have of work completed up to the cancel.
            _runs.FinishRun(runId, "cancelled",
                examined: examinedSoFar, added: prevAdded, updated: prevUpdated, errorCount: prevErrors);
        }
        catch (Exception ex)
        {
            StatusText = $"Failed: {ex.GetType().Name}: {ex.Message}";
            _runs.FinishRun(runId, "failed",
                examined: examinedSoFar, added: prevAdded, updated: prevUpdated, errorCount: prevErrors,
                notes: $"{ex.GetType().Name}: {ex.Message}");
        }
        finally { if (!_runningMaintenance) IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RefreshExistingAsync()
    {
        IsBusy = true;
        var ct = BeginSession();
        StatusText = "Revisiting URLs for existing jobs to backfill salary and description...";
        var runId = _runs.StartRun("refresh_existing", "max=250");
        try
        {
            var progress = StepProgressFor(runId);
            var r = await Task.Run(() => _detailRefresher.RefreshExistingAsync(max: 250, progress: progress, ct: ct), ct).ConfigureAwait(true);
            StatusText = $"Refreshed existing: {r.Examined} examined, {r.Updated} updated, " +
                         $"{r.NoChange} unchanged, {r.Failed} failed."
                         + (r.Errors.Count > 0 ? $"  First error: {r.Errors[0]}" : "");
            _runs.FinishRun(runId, r.Errors.Count == 0 ? "completed" : "partial",
                examined: r.Examined, updated: r.Updated, skipped: r.NoChange, failed: r.Failed,
                errorCount: r.Errors.Count,
                notes: r.Errors.Count == 0 ? null : string.Join(" | ", r.Errors.Take(5)));
            Completed?.Invoke();
        }
        catch (OperationCanceledException)
        {
            StatusText = CancelledStatusText();
            _runs.FinishRun(runId, "cancelled");
        }
        catch (Exception ex)
        {
            StatusText = $"Failed: {ex.GetType().Name}: {ex.Message}";
            _runs.FinishRun(runId, "failed", notes: $"{ex.GetType().Name}: {ex.Message}");
        }
        finally { if (!_runningMaintenance) IsBusy = false; }
    }

    // GenerateSummariesAsync / RegenerateSummariesAsync removed — summary backfill is now
    // continuous via SummaryWorker, which polls job_processing_queue rows of task_type='summary'.
    // The IJobSummarizer service itself is still around so the worker can call SummarizeAsync.

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void ReclassifyJobs()
    {
        IsBusy = true;
        var runId = _runs.StartRun("reclassify_jobs");
        try
        {
            var r = _reclassifier.ReclassifyAll();
            StatusText = $"Reclassified: {r.Examined} examined, {r.Changed} updated with new level/area.";
            _runs.FinishRun(runId, "completed", examined: r.Examined, updated: r.Changed);
            Completed?.Invoke();
        }
        catch (Exception ex)
        {
            StatusText = $"Failed: {ex.GetType().Name}: {ex.Message}";
            _runs.FinishRun(runId, "failed", notes: $"{ex.GetType().Name}: {ex.Message}");
        }
        finally { if (!_runningMaintenance) IsBusy = false; }
    }

    // MatchResumeAsync removed — resume scoring is now continuous via ResumeMatchWorker, which
    // batches dequeued queue rows and calls IResumeMatcher.MatchSubsetAsync.

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void PruneOutOfArea()
    {
        IsBusy = true;
        var runId = _runs.StartRun("prune_out_of_area");
        try
        {
            var rows = _jobs.GetActiveLocations();
            var removed = 0;
            var examined = rows.Count;
            var now = DateTime.UtcNow;
            foreach (var (id, location) in rows)
            {
                if (LocationMatcher.IsVancouverArea(location)) continue;
                _jobs.MarkRemoved(id, now);
                removed++;
            }
            StatusText = $"Pruned {removed} job(s) whose location is clearly outside Vancouver area.";
            _runs.FinishRun(runId, "completed", examined: examined, failed: removed);
            Completed?.Invoke();
        }
        catch (Exception ex)
        {
            StatusText = $"Failed: {ex.GetType().Name}: {ex.Message}";
            _runs.FinishRun(runId, "failed", notes: $"{ex.GetType().Name}: {ex.Message}");
        }
        finally { if (!_runningMaintenance) IsBusy = false; }
    }
}
