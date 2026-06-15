using System;
using Dapper;

namespace Jobnet.Data.Repositories;

public sealed class AiExtractionCacheRepository : IAiExtractionCacheRepository
{
    private readonly IDbConnectionFactory _connections;

    public AiExtractionCacheRepository(IDbConnectionFactory connections)
    {
        _connections = connections;
    }

    public string? GetIfFresh(string url, string contentHash, int ttlHours)
    {
        if (ttlHours <= 0) return null;
        using var conn = _connections.Open();
        var row = conn.QuerySingleOrDefault<(string Hash, string CachedAt, string JobsJson)>(@"
            SELECT content_hash AS Hash, cached_at AS CachedAt, jobs_json AS JobsJson
            FROM ai_extraction_cache WHERE url = @url", new { url });
        if (row.Hash is null) return null;
        if (!string.Equals(row.Hash, contentHash, StringComparison.Ordinal)) return null;
        if (!DateTime.TryParse(row.CachedAt, out var cachedAt)) return null;
        var ageHours = (DateTime.UtcNow - cachedAt.ToUniversalTime()).TotalHours;
        if (ageHours > ttlHours) return null;

        conn.Execute("UPDATE ai_extraction_cache SET hit_count = hit_count + 1 WHERE url = @url",
            new { url });
        return row.JobsJson;
    }

    public string? GetByUrlIfRecent(string url, int withinHours)
    {
        if (withinHours <= 0) return null;
        using var conn = _connections.Open();
        var row = conn.QuerySingleOrDefault<(string CachedAt, string JobsJson)>(@"
            SELECT cached_at AS CachedAt, jobs_json AS JobsJson
            FROM ai_extraction_cache WHERE url = @url", new { url });
        if (row.JobsJson is null) return null;
        if (!DateTime.TryParse(row.CachedAt, out var cachedAt)) return null;
        if ((DateTime.UtcNow - cachedAt.ToUniversalTime()).TotalHours > withinHours) return null;
        // Bump hit_count so we can distinguish "skipped by recency" from "skipped by content hash"
        // — same column, but the gap between cached_at and the hit will be small (within the
        // skip window) which is a cheap way to spot recency skips post-hoc if needed.
        conn.Execute("UPDATE ai_extraction_cache SET hit_count = hit_count + 1 WHERE url = @url",
            new { url });
        return row.JobsJson;
    }

    public void Put(string url, string contentHash, string jobsJson)
    {
        using var conn = _connections.Open();
        conn.Execute(@"
            INSERT INTO ai_extraction_cache (url, content_hash, cached_at, jobs_json, hit_count)
            VALUES (@url, @contentHash, @cachedAt, @jobsJson, 0)
            ON CONFLICT(url) DO UPDATE SET
                content_hash = excluded.content_hash,
                cached_at    = excluded.cached_at,
                jobs_json    = excluded.jobs_json,
                hit_count    = 0",
            new
            {
                url,
                contentHash,
                cachedAt = DateTime.UtcNow.ToString("o"),
                jobsJson,
            });
    }
}
