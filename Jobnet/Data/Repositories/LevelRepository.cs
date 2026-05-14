using System.Collections.Generic;
using System.Linq;
using Dapper;
using Jobnet.Models;

namespace Jobnet.Data.Repositories;

public sealed class LevelRepository : ILevelRepository
{
    private readonly IDbConnectionFactory _connections;

    public LevelRepository(IDbConnectionFactory connections)
    {
        _connections = connections;
    }

    public IReadOnlyList<Level> GetAll()
    {
        using var conn = _connections.Open();
        return conn.Query<LevelRow>("SELECT id, name, sort_order FROM levels ORDER BY sort_order, name")
            .Select(r => new Level { Id = r.Id, Name = r.Name, SortOrder = r.SortOrder })
            .ToList();
    }

    public Level? GetByName(string name)
    {
        using var conn = _connections.Open();
        var r = conn.QuerySingleOrDefault<LevelRow>(
            "SELECT id, name, sort_order FROM levels WHERE LOWER(name) = LOWER(@name)",
            new { name });
        return r is null ? null : new Level { Id = r.Id, Name = r.Name, SortOrder = r.SortOrder };
    }

    public int Insert(string name, int sortOrder)
    {
        using var conn = _connections.Open();
        return (int)conn.ExecuteScalar<long>(@"
            INSERT INTO levels (name, sort_order) VALUES (@name, @sortOrder);
            SELECT last_insert_rowid();",
            new { name, sortOrder });
    }

    public void Update(Level level)
    {
        using var conn = _connections.Open();
        conn.Execute("UPDATE levels SET name = @Name, sort_order = @SortOrder WHERE id = @Id", level);
    }

    public void Delete(int id)
    {
        using var conn = _connections.Open();
        conn.Execute("DELETE FROM levels WHERE id = @id", new { id });
    }

    public void Reorder(IReadOnlyList<int> orderedIds)
    {
        using var conn = _connections.Open();
        using var tx = conn.BeginTransaction();
        for (var i = 0; i < orderedIds.Count; i++)
            conn.Execute("UPDATE levels SET sort_order = @i WHERE id = @id",
                new { i, id = orderedIds[i] }, tx);
        tx.Commit();
    }

    private sealed class LevelRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int SortOrder { get; set; }
    }
}
