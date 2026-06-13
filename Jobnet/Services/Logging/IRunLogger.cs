using System.Collections.Generic;

namespace Jobnet.Services.Logging;

public interface IRunLogger
{
    /// <summary>Open a new run. Returns the run id (use it for steps + FinishRun).</summary>
    long StartRun(string runType, string? scope = null);

    /// <summary>Open a new step within a run. Returns the step id.</summary>
    long StartStep(long runId, string stepName);

    /// <summary>Close a step. Counts default to 0.
    /// <paramref name="outcomeKind"/> is the finer-grained classification from
    /// <see cref="OutcomeKind"/> — pass it whenever the caller knows *why* the step ended
    /// the way it did (e.g. "fetch_4xx", "ai_hallucination_rejected"). Status remains the
    /// coarse run-level rollup; outcome_kind is what `runs show` and dashboards filter on.</summary>
    void FinishStep(long stepId, string status,
                     int examined = 0, int added = 0, int updated = 0,
                     int skipped = 0, int failed = 0, string? errorMessage = null,
                     string? outcomeKind = null);

    /// <summary>Record one stage attempt within a company refresh. Multiple attempts per company
    /// are expected — the JobRefresher cascades through ats_api → cached_url → ai_extract. Use
    /// <see cref="Logging.AttemptStage"/> + <see cref="Logging.AttemptResult"/> for stage / result
    /// values so spellings stay consistent across the codebase.</summary>
    void LogAttempt(long? runId, int? companyId, string stage, string? stageDetail,
                    string result, int? httpStatus, int jobsYielded, long durationMs,
                    string? errorMessage);

    /// <summary>Record an AI extraction decision after the model returned. <paramref name="rawJobsCount"/>
    /// is what the model said; <paramref name="acceptedCount"/> is what survived our filters
    /// (citation check, location, dedup). The diff is what we rejected — and is the hallucination
    /// signal.</summary>
    void LogAiDecision(long? runId, int? companyId, string? sourceUrl, string provider,
                       int rawJobsCount, int acceptedCount, int citationVerifiedCount,
                       string? rejectedTitlesJson, bool suspectedHallucination);

    /// <summary>Close a run. Aggregates may come from caller; we also recompute from steps for safety.</summary>
    void FinishRun(long runId, string status,
                    int examined = 0, int added = 0, int updated = 0,
                    int skipped = 0, int failed = 0, int errorCount = 0,
                    string? notes = null);

    /// <summary>Recent runs in reverse chronological order.</summary>
    IReadOnlyList<RunSummary> GetRecent(int limit = 50);

    IReadOnlyList<StepSummary> GetSteps(long runId);

    /// <summary>Most recent started_at for a run of the given type whose scope text contains
    /// the substring. Returns null if there is no matching row.</summary>
    System.DateTime? GetLastRunStartedAt(string runType, string scopeContains);

    /// <summary>Most recent started_at for any run of the given type. Returns null if none recorded.</summary>
    System.DateTime? GetLastRunStartedAt(string runType);

    /// <summary>Mark any run/step rows that are still "running" as "interrupted" and backfill
    /// their aggregate counts from completed step rows. Call once on application startup so
    /// process kills / crashes don't leave the history page showing forever-running entries.
    /// Returns how many run_log rows were touched.</summary>
    int CleanupDanglingRuns();
}

public sealed class RunSummary
{
    public required long Id { get; init; }
    public required string RunType { get; init; }
    public string? Scope { get; init; }
    public required System.DateTime StartedAt { get; init; }
    public System.DateTime? FinishedAt { get; init; }
    public int? DurationMs { get; init; }
    public required string Status { get; init; }
    public int Examined { get; init; }
    public int Added { get; init; }
    public int Updated { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public int ErrorCount { get; init; }
    public string? Notes { get; init; }
}

public sealed class StepSummary
{
    public required long Id { get; init; }
    public required long RunId { get; init; }
    public required string StepName { get; init; }
    public required System.DateTime StartedAt { get; init; }
    public System.DateTime? FinishedAt { get; init; }
    public int? DurationMs { get; init; }
    public required string Status { get; init; }
    public int Examined { get; init; }
    public int Added { get; init; }
    public int Updated { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>One progress tick from a batch service (summary backfill, detail refresh, resume
/// match, etc.). VMs convert these into run_step_log rows. Stage is "starting" before work
/// begins on an item, "done" after — so the UI can show motion even on slow items.</summary>
public sealed class BatchStepProgress
{
    public required string Name { get; init; }
    public required string Stage { get; init; }   // "starting" | "done"
    /// <summary>Only meaningful when Stage = "done". One of: completed | skipped | failed | partial.</summary>
    public string? Status { get; init; }
    public int Added { get; init; }
    public int Updated { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public string? ErrorMessage { get; init; }
}
