using System.Collections.Generic;
using System.Linq;
using Dapper;
using Jobnet.Models;

namespace Jobnet.Data.Repositories;

public sealed class AggregatorRepository : IAggregatorRepository
{
    private readonly IDbConnectionFactory _connections;

    public AggregatorRepository(IDbConnectionFactory connections)
    {
        _connections = connections;
    }

    public IReadOnlyList<AggregatorSource> GetAll()
    {
        using var conn = _connections.Open();
        return conn.Query<(int Id, string Name, string BaseUrl, string? SearchUrlTemplate,
                            long IsEnabled, string? Notes, int MaxPages)>(@"
            SELECT id, name, base_url AS BaseUrl, search_url_template AS SearchUrlTemplate,
                   is_enabled AS IsEnabled, notes, max_pages AS MaxPages
            FROM aggregator_sources
            ORDER BY name")
            .Select(r => new AggregatorSource
            {
                Id = r.Id,
                Name = r.Name,
                BaseUrl = r.BaseUrl,
                SearchUrlTemplate = r.SearchUrlTemplate,
                IsEnabled = r.IsEnabled != 0,
                Notes = r.Notes,
                MaxPages = r.MaxPages <= 0 ? 1 : r.MaxPages,
            })
            .ToList();
    }

    public void SetEnabled(int id, bool enabled)
    {
        using var conn = _connections.Open();
        conn.Execute(
            "UPDATE aggregator_sources SET is_enabled = @enabled WHERE id = @id",
            new { id, enabled = enabled ? 1 : 0 });
    }

    public int Insert(string name, string baseUrl, string? notes, bool isEnabled, int maxPages)
    {
        using var conn = _connections.Open();
        return (int)conn.ExecuteScalar<long>(@"
            INSERT INTO aggregator_sources (name, base_url, is_enabled, notes, max_pages)
            VALUES (@name, @baseUrl, @isEnabled, @notes, @maxPages);
            SELECT last_insert_rowid();",
            new { name, baseUrl, notes, isEnabled = isEnabled ? 1 : 0, maxPages });
    }

    public void Update(int id, string name, string baseUrl, string? notes, bool isEnabled, int maxPages)
    {
        using var conn = _connections.Open();
        conn.Execute(@"
            UPDATE aggregator_sources SET
                name = @name, base_url = @baseUrl, is_enabled = @isEnabled,
                notes = @notes, max_pages = @maxPages
            WHERE id = @id",
            new { id, name, baseUrl, isEnabled = isEnabled ? 1 : 0, notes, maxPages });
    }

    public void Delete(int id)
    {
        using var conn = _connections.Open();
        conn.Execute("DELETE FROM aggregator_sources WHERE id = @id", new { id });
    }
}
