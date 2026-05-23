using System.Collections.Generic;
using System.Linq;
using Jobnet.Data.Repositories;

namespace Jobnet.Services.Discovery.Strategies;

public sealed class DiscoveryStrategyProvider : IDiscoveryStrategyProvider
{
    private readonly WebSearchStrategy _webSearch;
    private readonly AiCompetitorStrategy _aiCompetitor;
    private readonly ICompanyDirectoryHarvester _harvester;
    private readonly IDiscoverySeedRepository _seeds;
    private readonly IAggregatorRepository _boards;

    public DiscoveryStrategyProvider(WebSearchStrategy webSearch,
                                      AiCompetitorStrategy aiCompetitor,
                                      ICompanyDirectoryHarvester harvester,
                                      IDiscoverySeedRepository seeds,
                                      IAggregatorRepository boards)
    {
        _webSearch = webSearch;
        _aiCompetitor = aiCompetitor;
        _harvester = harvester;
        _seeds = seeds;
        _boards = boards;
    }

    public IDiscoveryStrategy GetWebSearch() => _webSearch;

    public IReadOnlyList<IDiscoveryStrategy> GetDirectoryStrategies()
    {
        return _seeds.GetEnabled().Select(s =>
            (IDiscoveryStrategy)new DirectoryHarvestStrategy(
                s.Name,
                s.MaxPages > 1
                    ? $"{s.Description ?? "(no description)"} (paginated, up to {s.MaxPages} pages)"
                    : (s.Description ?? "(no description)"),
                s.Url,
                _harvester,
                sourceType: "directory",
                maxPages: s.MaxPages)).ToList();
    }

    public IReadOnlyList<IDiscoveryStrategy> GetBoardStrategies()
    {
        return _boards.GetAll()
            .Where(b => b.IsEnabled && !string.IsNullOrWhiteSpace(b.BaseUrl))
            .Select(b => (IDiscoveryStrategy)new DirectoryHarvestStrategy(
                b.Name,
                b.MaxPages > 1
                    ? $"{b.Notes ?? $"Board: {b.BaseUrl}"} (paginated, up to {b.MaxPages} pages)"
                    : (b.Notes ?? $"Board: {b.BaseUrl}"),
                b.BaseUrl,
                _harvester,
                sourceType: "board",
                maxPages: b.MaxPages)).ToList();
    }

    public IReadOnlyList<IDiscoveryStrategy> GetAll()
    {
        var list = new List<IDiscoveryStrategy> { _webSearch, _aiCompetitor };
        list.AddRange(GetDirectoryStrategies());
        list.AddRange(GetBoardStrategies());
        list.Add(new BatchHarvestStrategy(() => list));
        return list;
    }
}
