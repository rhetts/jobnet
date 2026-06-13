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
