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
    public required int ResultsSkippedFiltered { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }
    public required IReadOnlyList<string> AddedDomains { get; init; }
}
