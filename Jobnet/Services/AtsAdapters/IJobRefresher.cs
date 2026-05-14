using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Models;

namespace Jobnet.Services.AtsAdapters;

public interface IJobRefresher
{
    Task<JobRefreshReport> RefreshAsync(Company company, CancellationToken ct = default);
    Task<JobRefreshReport> RefreshAllAsync(CancellationToken ct = default);
}

public sealed class JobRefreshReport
{
    public int CompaniesProcessed { get; init; }
    public int CompaniesSkippedNoAts { get; init; }
    public int CompaniesFailed { get; init; }
    public int JobsAdded { get; init; }
    public int JobsUpdated { get; init; }
    public int JobsRemoved { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = System.Array.Empty<string>();
}
