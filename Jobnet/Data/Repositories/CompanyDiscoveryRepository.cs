using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using Jobnet.Models;

namespace Jobnet.Data.Repositories;

public sealed class CompanyDiscoveryRepository : ICompanyDiscoveryRepository
{
    private readonly IDbConnectionFactory _connections;

    public CompanyDiscoveryRepository(IDbConnectionFactory connections)
    {
        _connections = connections;
    }

    public void Record(int companyId, string sourceType, string sourceName, string? sourceUrl, long? runId)
    {
        using var conn = _connections.Open();
        conn.Execute(@"
            INSERT INTO company_discoveries (company_id, source_type, source_name, source_url, run_id, discovered_at)
            VALUES (@companyId, @sourceType, @sourceName, @sourceUrl, @runId, @when)",
            new { companyId, sourceType, sourceName, sourceUrl, runId, when = DateTime.UtcNow.ToString("o") });
    }

    public IReadOnlyList<CompanyDiscovery> GetByCompany(int companyId)
    {
        using var conn = _connections.Open();
        return conn.Query<DiscRow>(@"
            SELECT id, company_id AS CompanyId, source_type AS SourceType,
                   source_name AS SourceName, source_url AS SourceUrl,
                   run_id AS RunId, discovered_at AS DiscoveredAt
            FROM company_discoveries
            WHERE company_id = @companyId
            ORDER BY discovered_at DESC", new { companyId })
            .Select(Map).ToList();
    }

    public int CountDistinctSources(int companyId)
    {
        using var conn = _connections.Open();
        return conn.ExecuteScalar<int>(@"
            SELECT COUNT(DISTINCT source_name) FROM company_discoveries WHERE company_id = @companyId",
            new { companyId });
    }

    private static CompanyDiscovery Map(DiscRow r) => new()
    {
        Id = r.Id,
        CompanyId = r.CompanyId,
        SourceType = r.SourceType,
        SourceName = r.SourceName,
        SourceUrl = r.SourceUrl,
        RunId = r.RunId,
        DiscoveredAt = DateTime.Parse(r.DiscoveredAt).ToUniversalTime(),
    };

    private sealed class DiscRow
    {
        public int Id { get; set; }
        public int CompanyId { get; set; }
        public string SourceType { get; set; } = "";
        public string SourceName { get; set; } = "";
        public string? SourceUrl { get; set; }
        public long? RunId { get; set; }
        public string DiscoveredAt { get; set; } = "";
    }
}
