using System.Collections.Generic;
using Jobnet.Models;

namespace Jobnet.Data.Repositories;

public interface ICompanyDiscoveryRepository
{
    /// <summary>Record one sighting of a company in a source. Always inserts a new row
    /// (the table is a history log, not a unique mapping).</summary>
    void Record(int companyId, string sourceType, string sourceName, string? sourceUrl, long? runId);

    /// <summary>All sightings for one company, most recent first.</summary>
    IReadOnlyList<CompanyDiscovery> GetByCompany(int companyId);

    /// <summary>Count of distinct sources that have surfaced this company.</summary>
    int CountDistinctSources(int companyId);
}
