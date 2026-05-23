using System;
using Dapper;

namespace Jobnet.Data.Repositories;

public sealed class DirectoryCrawlRepository : IDirectoryCrawlRepository
{
    private readonly IDbConnectionFactory _connections;

    public DirectoryCrawlRepository(IDbConnectionFactory connections)
    {
        _connections = connections;
    }

    public DateTime? GetLastCrawlUtc(string url)
    {
        using var conn = _connections.Open();
        var raw = conn.ExecuteScalar<string?>(@"
            SELECT fetched_at FROM directory_crawls
            WHERE url = @url AND success = 1
            ORDER BY fetched_at DESC LIMIT 1", new { url });
        if (string.IsNullOrEmpty(raw)) return null;
        return DateTime.TryParse(raw, out var dt) ? dt.ToUniversalTime() : null;
    }

    public void Record(string url, DateTime fetchedAtUtc, int? durationMs,
                        int candidatesFound, int candidatesAdded, bool success, string? error)
    {
        using var conn = _connections.Open();
        conn.Execute(@"
            INSERT INTO directory_crawls
                (url, fetched_at, duration_ms, candidates_found, candidates_added, success, error_message)
            VALUES (@url, @fetchedAt, @durationMs, @candidatesFound, @candidatesAdded, @success, @error)",
            new
            {
                url,
                fetchedAt = fetchedAtUtc.ToUniversalTime().ToString("o"),
                durationMs,
                candidatesFound,
                candidatesAdded,
                success = success ? 1 : 0,
                error,
            });
    }
}
