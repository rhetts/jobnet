using System.Collections.Generic;
using System.Linq;
using Dapper;
using Jobnet.Models;

namespace Jobnet.Data.Repositories;

public sealed class AreaRepository : IAreaRepository
{
    private readonly IDbConnectionFactory _connections;

    public AreaRepository(IDbConnectionFactory connections)
    {
        _connections = connections;
    }

    public IReadOnlyList<Area> GetAll()
    {
        using var conn = _connections.Open();
        return conn.Query<AreaRow>("SELECT id, name, sort_order FROM areas ORDER BY sort_order, name")
            .Select(r => new Area { Id = r.Id, Name = r.Name, SortOrder = r.SortOrder })
            .ToList();
    }

    public Area? GetByName(string name)
    {
        using var conn = _connections.Open();
        var r = conn.QuerySingleOrDefault<AreaRow>(
            "SELECT id, name, sort_order FROM areas WHERE LOWER(name) = LOWER(@name)",
            new { name });
        return r is null ? null : new Area { Id = r.Id, Name = r.Name, SortOrder = r.SortOrder };
    }

    public int Insert(string name, int sortOrder)
    {
        using var conn = _connections.Open();
        return (int)conn.ExecuteScalar<long>(@"
            INSERT INTO areas (name, sort_order) VALUES (@name, @sortOrder);
            SELECT last_insert_rowid();",
            new { name, sortOrder });
    }

    public void Update(Area area)
    {
        using var conn = _connections.Open();
        conn.Execute("UPDATE areas SET name = @Name, sort_order = @SortOrder WHERE id = @Id", area);
    }

    public void Delete(int id)
    {
        using var conn = _connections.Open();
        conn.Execute("DELETE FROM areas WHERE id = @id", new { id });
    }

    public void Reorder(IReadOnlyList<int> orderedIds)
    {
        using var conn = _connections.Open();
        using var tx = conn.BeginTransaction();
        for (var i = 0; i < orderedIds.Count; i++)
            conn.Execute("UPDATE areas SET sort_order = @i WHERE id = @id",
                new { i, id = orderedIds[i] }, tx);
        tx.Commit();
    }

    public IReadOnlyList<int> GetAreaIdsForJob(int jobId)
    {
        using var conn = _connections.Open();
        return conn.Query<int>("SELECT area_id FROM job_areas WHERE job_id = @jobId", new { jobId }).ToList();
    }

    public void SetAreasForJob(int jobId, IReadOnlyList<int> areaIds)
    {
        using var conn = _connections.Open();
        using var tx = conn.BeginTransaction();
        conn.Execute("DELETE FROM job_areas WHERE job_id = @jobId", new { jobId }, tx);
        foreach (var areaId in areaIds.Distinct())
            conn.Execute("INSERT INTO job_areas (job_id, area_id) VALUES (@jobId, @areaId)",
                new { jobId, areaId }, tx);
        tx.Commit();
    }

    private sealed class AreaRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int SortOrder { get; set; }
    }
}
