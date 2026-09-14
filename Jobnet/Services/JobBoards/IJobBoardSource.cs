using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jobnet.Services.JobBoards;

/// <summary>
/// A page that lists job postings from *many* unrelated employers at once (a job-search
/// aggregator, as opposed to one company's own careers page). Neither <c>IJobSource</c>/
/// <c>IHtmlPatternParser</c> (one company) nor <c>IDirectoryPatternParser</c> (many companies,
/// no job details) fit this shape — a board source resolves or creates the right
/// <c>Company</c> row per posting and inserts the job directly, in one pass.
/// </summary>
public interface IJobBoardSource
{
    /// <summary>Stable identifier for logging (run_step_log, status text).</summary>
    string Name { get; }

    Task<JobBoardIngestResult> IngestAsync(CancellationToken ct = default);
}

public sealed class JobBoardIngestResult
{
    public int PagesProcessed { get; set; }
    public int RowsExamined { get; set; }
    public int CompaniesCreated { get; set; }
    public int JobsAdded { get; set; }
    public int JobsUpdated { get; set; }
    public int SkippedUnmatchedCompany { get; set; }
    public int SkippedOutOfArea { get; set; }
    public List<string> Errors { get; } = new();
}
