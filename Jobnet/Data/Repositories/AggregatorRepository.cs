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
        return conn.Query<(int Id, string Name, string BaseUrl, string? SearchUrlTemplate, long IsEnabled, string? Notes)>(@"
            SELECT id, name, base_url AS BaseUrl, search_url_template AS SearchUrlTemplate,
                   is_enabled AS IsEnabled, notes
            FROM aggregator_sources
            ORDER BY name")
            .Select(r => new AggregatorSource
            {
                Id = r.Id,
                Name = r.Name,
                BaseUrl = r.BaseUrl,
                SearchUrlTemplate = r.SearchUrlTemplate,
                IsEnabled = r.IsEnabled != 0,
                Notes = r.Notes
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
}
