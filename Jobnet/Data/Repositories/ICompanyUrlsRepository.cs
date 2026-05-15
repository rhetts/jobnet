using System;
using System.Collections.Generic;
using Jobnet.Models;

namespace Jobnet.Data.Repositories;

public interface ICompanyUrlsRepository
{
    IReadOnlyList<CompanyUrl> GetByCompany(int companyId);
    IReadOnlyList<CompanyUrl> GetByCompanyAndKind(int companyId, string kind);

    /// <summary>Insert if new; otherwise bump last_seen, label, discovered_via.</summary>
    void Upsert(int companyId, string url, string kind, string? label = null, string? discoveredVia = null);

    /// <summary>Mark this URL as having produced at least one job today.</summary>
    void MarkYielded(int companyId, string url);

    /// <summary>Increment fail_count; delete after N consecutive failures.</summary>
    void RecordFailure(int companyId, string url, int deleteAfter = 2);

    void Delete(int companyId, string url);
    int DeleteStale(int notYieldedDays);
}
