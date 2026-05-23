using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Models;

namespace Jobnet.Services.AtsAdapters;

public interface IJobRefresher
{
    Task<JobRefreshReport> RefreshAsync(Company company, CancellationToken ct = default);
    /// <summary>Refresh every known company. If <paramref name="minDaysSinceLastScan"/> &gt; 0,
    /// companies whose <c>DateLastScan</c> is within that window are skipped (their counts go into
    /// <see cref="JobRefreshReport.CompaniesSkippedRecent"/>). If <paramref name="progress"/> is
    /// supplied, the refresher reports a tick after each company finishes processing — useful for
    /// driving a live status bar in the UI since this method can run for hours on a full sweep.</summary>
    Task<JobRefreshReport> RefreshAllAsync(int minDaysSinceLastScan = 0, IProgress<JobRefreshProgress>? progress = null, CancellationToken ct = default);
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
