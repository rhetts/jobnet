using System;
using System.Globalization;
using System.Linq;
using Dapper;

namespace Jobnet.Data.Repositories;

public sealed class JobBoardCompanyMapRepository : IJobBoardCompanyMapRepository
{
    private readonly IDbConnectionFactory _connections;

    public JobBoardCompanyMapRepository(IDbConnectionFactory connections)
    {
        _connections = connections;
    }

    public JobBoardCompanyMapEntry? Get(string source, string companyName)
    {
        using var conn = _connections.Open();
        var row = conn.Query<Row>(@"
            SELECT domain AS Domain, resolved_at AS ResolvedAt
            FROM jobboard_company_map
            WHERE source = @source AND company_name = @companyName",
            new { source, companyName }).FirstOrDefault();
        if (row is null) return null;
        var resolvedAt = DateTime.TryParse(row.ResolvedAt, CultureInfo.InvariantCulture,
                                           DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t : DateTime.MinValue;
        return new JobBoardCompanyMapEntry(row.Domain, resolvedAt);
    }

    public void Save(string source, string companyName, string? domain)
    {
        using var conn = _connections.Open();
        conn.Execute(@"
            INSERT INTO jobboard_company_map (source, company_name, domain, resolved_at)
            VALUES (@source, @companyName, @domain, @resolvedAt)
            ON CONFLICT(source, company_name) DO UPDATE SET
                domain = excluded.domain,
                resolved_at = excluded.resolved_at",
            new { source, companyName, domain, resolvedAt = DateTime.UtcNow.ToString("o") });
    }

    private sealed class Row
    {
        public string? Domain { get; set; }
        public string ResolvedAt { get; set; } = "";
    }
}
