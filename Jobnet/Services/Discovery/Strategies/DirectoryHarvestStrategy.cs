using System.Threading;
using System.Threading.Tasks;

namespace Jobnet.Services.Discovery.Strategies;

/// <summary>A named strategy that harvests a single hardcoded directory or portfolio URL.</summary>
public sealed class DirectoryHarvestStrategy : IDiscoveryStrategy
{
    private readonly ICompanyDirectoryHarvester _harvester;
    private readonly string _url;
    private readonly string _sourceType;
    private readonly int _maxPages;

    public string Name { get; }
    public string Description { get; }

    public DirectoryHarvestStrategy(string name, string description, string url,
                                     ICompanyDirectoryHarvester harvester,
                                     string sourceType = "directory",
                                     int maxPages = 1)
    {
        Name = name;
        Description = description;
        _url = url;
        _harvester = harvester;
        _sourceType = sourceType;
        _maxPages = maxPages;
    }

    public async Task<StrategyReport> RunAsync(CancellationToken ct = default)
    {
        var r = await _harvester.HarvestAsync(_url, Name, _sourceType, _maxPages, ct);
        var rep = new StrategyReport
        {
            CandidatesExamined = r.CandidatesFound,
            CompaniesAdded = r.CompaniesAdded,
            CompaniesSkippedExisting = r.CompaniesSkippedExisting,
            CompaniesSkippedFiltered = r.CompaniesSkippedFiltered,
        };
        foreach (var e in r.Errors) rep.Errors.Add($"[{_url}] {e}");
        return rep;
    }
}
