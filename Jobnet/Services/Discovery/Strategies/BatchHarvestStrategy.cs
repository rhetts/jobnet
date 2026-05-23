using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jobnet.Services.Discovery.Strategies;

/// <summary>Runs every URL-based DirectoryHarvestStrategy in sequence and aggregates results.
/// Skips the WebSearchStrategy and itself.</summary>
public sealed class BatchHarvestStrategy : IDiscoveryStrategy
{
    private readonly System.Func<IEnumerable<IDiscoveryStrategy>> _all;

    public BatchHarvestStrategy(System.Func<IEnumerable<IDiscoveryStrategy>> all) { _all = all; }

    public string Name => "Run all directory strategies";
    public string Description => "Sequentially harvests every baked-in directory/portfolio URL. " +
                                  "Takes a few minutes and uses a Gemini call per source.";

    public async Task<StrategyReport> RunAsync(CancellationToken ct = default)
    {
        var rep = new StrategyReport();
        foreach (var s in _all().OfType<DirectoryHarvestStrategy>())
        {
            ct.ThrowIfCancellationRequested();
            var r = await s.RunAsync(ct);
            rep.CandidatesExamined += r.CandidatesExamined;
            rep.CompaniesAdded += r.CompaniesAdded;
            rep.CompaniesSkippedExisting += r.CompaniesSkippedExisting;
            rep.CompaniesSkippedFiltered += r.CompaniesSkippedFiltered;
            foreach (var e in r.Errors) rep.Errors.Add(e);
        }
        return rep;
    }
}
