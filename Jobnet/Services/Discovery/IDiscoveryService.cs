using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jobnet.Services.Discovery;

public interface IDiscoveryService
{
    Task<DiscoveryReport> RunAsync(int maxQueriesPerTerm = 1, CancellationToken ct = default);
}

public sealed class DiscoveryReport
{
    public required int QueriesIssued { get; init; }
    public required int ResultsExamined { get; init; }
    public required int CompaniesAdded { get; init; }
    public required int CompaniesSkippedExisting { get; init; }
    /// <summary>Existing companies (no ATS yet) that an ATS board hit attached ats_type/ats_slug
    /// to. Also counted in <see cref="CompaniesSkippedExisting"/>.</summary>
    public int CompaniesAtsLinked { get; init; }
    public required int ResultsSkippedFiltered { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }
    public required IReadOnlyList<string> AddedDomains { get; init; }
}
