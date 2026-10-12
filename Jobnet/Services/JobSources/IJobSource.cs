using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jobnet.Services.JobSources;

public interface IJobSource
{
    /// <summary>The ats_type value this source handles (e.g. "greenhouse").</summary>
    string AtsType { get; }

    Task<IReadOnlyList<RawJobPosting>> FetchAsync(string slug, CancellationToken ct = default);
}

/// <summary>A source whose board can be narrowed server-side by keyword. For boards shared by
/// several companies where the ATS reports no department to filter on — Harris Computer's Workday
/// tenant hosts every Harris business, and only a search for "TouchBistro" isolates TouchBistro's
/// postings. Driven by <c>companies.ats_department_filter</c>.</summary>
public interface IKeywordFilteredJobSource
{
    Task<IReadOnlyList<RawJobPosting>> FetchAsync(string slug, string keyword, CancellationToken ct = default);
}
