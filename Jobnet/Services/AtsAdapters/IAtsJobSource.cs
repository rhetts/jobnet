using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jobnet.Services.AtsAdapters;

public interface IAtsJobSource
{
    /// <summary>The ats_type value this source handles (e.g. "greenhouse").</summary>
    string AtsType { get; }

    Task<IReadOnlyList<RawJobPosting>> FetchAsync(string slug, CancellationToken ct = default);
}
