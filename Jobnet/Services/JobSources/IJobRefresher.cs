using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Models;

namespace Jobnet.Services.JobSources;

public interface IJobRefresher
{
    /// <summary><paramref name="runId"/> is the parent <c>run_log.id</c> — when supplied, the
    /// refresher writes per-stage attempts into <c>refresh_attempt</c> tied to this run, and
    /// API calls made during the refresh are stamped with run + company in api_call_log. Pass
    /// null only when called outside a tracked run (one-off CLI parse-page etc.).</summary>
    Task<JobRefreshReport> RefreshAsync(Company company, CancellationToken ct = default, long? runId = null);
    /// <summary>Refresh every known company. If <paramref name="minDaysSinceLastScan"/> &gt; 0,
    /// companies whose <c>DateLastScan</c> is within that window are skipped (their counts go into
    /// <see cref="JobRefreshReport.CompaniesSkippedRecent"/>). If <paramref name="progress"/> is
    /// supplied, the refresher reports a tick after each company finishes processing — useful for
    /// driving a live status bar in the UI since this method can run for hours on a full sweep.
    /// <paramref name="runId"/> threads through to refresh_attempt/api_call_log telemetry.</summary>
    Task<JobRefreshReport> RefreshAllAsync(int minDaysSinceLastScan = 0, IProgress<JobRefreshProgress>? progress = null, CancellationToken ct = default, long? runId = null);
}

public sealed class JobRefreshProgress
{
    public required int Current { get; init; }            // 1-based index of the company currently being / just processed
    public required int Total { get; init; }              // total count of companies that will be visited (eligible — already excludes skip<Nd)
    public required string CompanyName { get; init; }     // friendly name of the company
    public required string CompanyDomain { get; init; }
    public required string Stage { get; init; }           // "starting" before work begins, "done" after — lets the UI show motion even when a single company takes minutes (local llama path).
    public required int JobsAddedSoFar { get; init; }
    public required int JobsUpdatedSoFar { get; init; }
    public required int ErrorsSoFar { get; init; }

    /// <summary>Set only on the "done" tick. Per-company outcome classification (one of the
    /// <see cref="Logging.OutcomeKind"/> constants). The ViewModel passes this into FinishStep
    /// so the run history can be sliced by outcome later.</summary>
    public string? OutcomeKind { get; init; }

    /// <summary>Set only on the "done" tick when this company errored. The actual exception
    /// message — gets persisted as <c>run_step_log.error_message</c> so <c>runs show</c> can
    /// surface it without joining anything.</summary>
    public string? ErrorMessage { get; init; }
}

public sealed class JobRefreshReport
{
    public int CompaniesProcessed { get; init; }
    public int CompaniesSkippedNoAts { get; init; }
    public int CompaniesSkippedRecent { get; init; }
    public int CompaniesFailed { get; init; }
    public int JobsAdded { get; init; }
    public int JobsUpdated { get; init; }
    public int JobsRemoved { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = System.Array.Empty<string>();
}
