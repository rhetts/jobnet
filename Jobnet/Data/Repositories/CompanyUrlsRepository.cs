using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using Jobnet.Models;

namespace Jobnet.Data.Repositories;

public sealed class CompanyUrlsRepository : ICompanyUrlsRepository
{
    private readonly IDbConnectionFactory _connections;

    public CompanyUrlsRepository(IDbConnectionFactory connections)
    {
        _connections = connections;
    }

    private const string SelectAll = @"
        SELECT id, company_id AS CompanyId, url, kind, label,
               discovered_via AS DiscoveredVia, fail_count AS FailCount,
               last_seen AS LastSeenIso, last_yielded AS LastYieldedIso
        FROM company_urls";

    public IReadOnlyList<CompanyUrl> GetByCompany(int companyId)
    {
        using var conn = _connections.Open();
        return conn.Query<Row>($"{SelectAll} WHERE company_id = @companyId ORDER BY kind, url",
            new { companyId }).Select(Map).ToList();
    }

    public IReadOnlyList<CompanyUrl> GetByCompanyAndKind(int companyId, string kind)
    {
        using var conn = _connections.Open();
        return conn.Query<Row>($"{SelectAll} WHERE company_id = @companyId AND kind = @kind ORDER BY url",
            new { companyId, kind }).Select(Map).ToList();
    }

    public void Upsert(int companyId, string url, string kind, string? label = null, string? discoveredVia = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        using var conn = _connections.Open();
        conn.Execute(@"
            INSERT INTO company_urls (company_id, url, kind, label, discovered_via, fail_count, last_seen)
            VALUES (@companyId, @url, @kind, @label, @via, 0, @now)
            ON CONFLICT(company_id, url) DO UPDATE SET
                kind            = excluded.kind,
                label           = COALESCE(excluded.label, label),
                discovered_via  = COALESCE(excluded.discovered_via, discovered_via),
                fail_count      = 0,
                last_seen       = excluded.last_seen",
            new { companyId, url, kind, label, via = discoveredVia, now = DateTime.UtcNow.ToString("o") });
    }

    public void MarkYielded(int companyId, string url)
    {
        using var conn = _connections.Open();
        conn.Execute(@"
            UPDATE company_urls SET last_yielded = @now, fail_count = 0, last_seen = @now
            WHERE company_id = @companyId AND url = @url",
            new { companyId, url, now = DateTime.UtcNow.ToString("o") });
    }

    public void RecordFailure(int companyId, string url, int deleteAfter = 2)
    {
        using var conn = _connections.Open();
        conn.Execute("UPDATE company_urls SET fail_count = fail_count + 1 WHERE company_id = @companyId AND url = @url",
            new { companyId, url });
        var count = conn.ExecuteScalar<int?>(
            "SELECT fail_count FROM company_urls WHERE company_id = @companyId AND url = @url",
            new { companyId, url });
        if (count.HasValue && count.Value >= deleteAfter)
        {
            conn.Execute("DELETE FROM company_urls WHERE company_id = @companyId AND url = @url",
                new { companyId, url });
        }
    }

    public void Delete(int companyId, string url)
    {
        using var conn = _connections.Open();
        conn.Execute("DELETE FROM company_urls WHERE company_id = @companyId AND url = @url",
            new { companyId, url });
    }

    public int DeleteStale(int notYieldedDays)
    {
        using var conn = _connections.Open();
        var cutoff = DateTime.UtcNow.AddDays(-notYieldedDays).ToString("o");
        return conn.Execute(@"
            DELETE FROM company_urls
            WHERE (last_yielded IS NULL AND last_seen < @cutoff)
               OR (last_yielded IS NOT NULL AND last_yielded < @cutoff)",
            new { cutoff });
    }

    private static CompanyUrl Map(Row r) => new()
    {
        Id = r.Id,
        CompanyId = r.CompanyId,
        Url = r.Url,
        Kind = r.Kind,
        Label = r.Label,
        DiscoveredVia = r.DiscoveredVia,
        FailCount = r.FailCount,
        LastSeen = DateTime.Parse(r.LastSeenIso).ToUniversalTime(),
        LastYielded = string.IsNullOrEmpty(r.LastYieldedIso) ? null : DateTime.Parse(r.LastYieldedIso).ToUniversalTime(),
    };

    private sealed class Row
    {
        public int Id { get; set; }
        public int CompanyId { get; set; }
        public string Url { get; set; } = "";
        public string Kind { get; set; } = "";
        public string? Label { get; set; }
        public string? DiscoveredVia { get; set; }
        public int FailCount { get; set; }
        public string LastSeenIso { get; set; } = "";
        public string? LastYieldedIso { get; set; }
    }
}
