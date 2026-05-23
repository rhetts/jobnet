using System.Threading;
using System.Threading.Tasks;

namespace Jobnet.Services.Discovery.Strategies;

/// <summary>Wraps the existing IDiscoveryService — runs every active query in the
/// search_terms table through Brave/Google CSE and adds new companies.</summary>
public sealed class WebSearchStrategy : IDiscoveryStrategy
{
    private readonly IDiscoveryService _discovery;
    public WebSearchStrategy(IDiscoveryService discovery) { _discovery = discovery; }

    public string Name => "Web search (Brave / Google CSE)";
    public string Description => "Run every active search term through the configured search engine, " +
                                  "filter out aggregators/big multinationals, and add new companies. " +
                                  "Generic queries surface well-known names; long-tail queries find startups.";

    public async Task<StrategyReport> RunAsync(CancellationToken ct = default)
    {
        var r = await _discovery.RunAsync(ct: ct);
        var rep = new StrategyReport
        {
            CandidatesExamined = r.ResultsExamined,
            CompaniesAdded = r.CompaniesAdded,
            CompaniesSkippedExisting = r.CompaniesSkippedExisting,
            CompaniesSkippedFiltered = r.ResultsSkippedFiltered,
        };
        foreach (var e in r.Errors) rep.Errors.Add(e);
        return rep;
    }
}
