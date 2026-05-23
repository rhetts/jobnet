using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using Jobnet.Models;

namespace Jobnet.Data.Repositories;

public sealed class SavedFilterRepository : ISavedFilterRepository
{
    private readonly IDbConnectionFactory _connections;

    public SavedFilterRepository(IDbConnectionFactory connections)
    {
        _connections = connections;
    }

    public IReadOnlyList<SavedFilter> GetAll()
    {
        using var conn = _connections.Open();
        return conn.Query<SavedFilterRow>(@"
            SELECT id, name, payload, date_created AS DateCreated, date_used AS DateUsed
            FROM saved_filters
            ORDER BY (date_used IS NULL), date_used DESC, name").Select(Map).ToList();
    }

    public SavedFilter? GetByName(string name)
    {
        using var conn = _connections.Open();
        var row = conn.QuerySingleOrDefault<SavedFilterRow>(@"
            SELECT id, name, payload, date_created AS DateCreated, date_used AS DateUsed
            FROM saved_filters WHERE LOWER(name) = LOWER(@name)", new { name });
        return row is null ? null : Map(row);
    }

    public int Upsert(string name, string payloadJson)
    {
        using var conn = _connections.Open();
        return (int)conn.ExecuteScalar<long>(@"
            INSERT INTO saved_filters (name, payload, date_created)
            VALUES (@name, @payload, @now)
            ON CONFLICT(name) DO UPDATE SET payload = excluded.payload
            RETURNING id;",
            new { name, payload = payloadJson, now = DateTime.UtcNow.ToString("o") });
    }

    public void Delete(int id)
    {
        using var conn = _connections.Open();
        conn.Execute("DELETE FROM saved_filters WHERE id = @id", new { id });
    }

    public void Rename(int id, string newName)
    {
        using var conn = _connections.Open();
        conn.Execute("UPDATE saved_filters SET name = @newName WHERE id = @id",
            new { id, newName });
    }

    public void MarkUsed(int id)
    {
        using var conn = _connections.Open();
        conn.Execute("UPDATE saved_filters SET date_used = @now WHERE id = @id",
            new { id, now = DateTime.UtcNow.ToString("o") });
    }

    private static SavedFilter Map(SavedFilterRow r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        Payload = r.Payload,
        DateCreated = DateTime.Parse(r.DateCreated).ToUniversalTime(),
        DateUsed = string.IsNullOrEmpty(r.DateUsed) ? null : DateTime.Parse(r.DateUsed).ToUniversalTime(),
    };

    private sealed class SavedFilterRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Payload { get; set; } = string.Empty;
        public string DateCreated { get; set; } = string.Empty;
        public string? DateUsed { get; set; }
    }
}
