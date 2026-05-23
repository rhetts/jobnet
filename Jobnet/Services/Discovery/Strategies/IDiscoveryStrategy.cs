using System.Threading;
using System.Threading.Tasks;

namespace Jobnet.Services.Discovery.Strategies;

/// <summary>One strategy for finding new companies — search-based, directory-harvest, batch, etc.
/// Each strategy returns a normalized DiscoveryReport so the UI can show a single status line.</summary>
public interface IDiscoveryStrategy
{
    /// <summary>Short label shown in the strategy selector dropdown.</summary>
    string Name { get; }
    /// <summary>Tooltip / hint text describing what this strategy does.</summary>
    string Description { get; }
    Task<StrategyReport> RunAsync(CancellationToken ct = default);
}

public sealed class StrategyReport
{
    public int CandidatesExamined { get; set; }
    public int CompaniesAdded { get; set; }
    public int CompaniesSkippedExisting { get; set; }
    public int CompaniesSkippedFiltered { get; set; }
    public System.Collections.Generic.List<string> Errors { get; } = new();
}
