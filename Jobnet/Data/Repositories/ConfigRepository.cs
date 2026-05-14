using System.Collections.Generic;
using System.Linq;
using Dapper;

namespace Jobnet.Data.Repositories;

public sealed class ConfigRepository : IConfigRepository
{
    private readonly IDbConnectionFactory _connections;

    public ConfigRepository(IDbConnectionFactory connections)
    {
        _connections = connections;
    }

    public string? Get(string key)
    {
        using var conn = _connections.Open();
        return conn.QuerySingleOrDefault<string?>(
            "SELECT value FROM config WHERE key = @key", new { key });
    }

    public string GetOrDefault(string key, string fallback) => Get(key) ?? fallback;

    public IReadOnlyDictionary<string, string> GetAll()
    {
        using var conn = _connections.Open();
        return conn.Query<(string Key, string Value)>("SELECT key, value FROM config ORDER BY key")
            .ToDictionary(r => r.Key, r => r.Value);
    }

    public void Set(string key, string value)
    {
        using var conn = _connections.Open();
        conn.Execute(@"
            INSERT INTO config (key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            new { key, value });
    }
}
