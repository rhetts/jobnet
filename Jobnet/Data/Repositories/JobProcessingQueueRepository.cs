using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;

namespace Jobnet.Data.Repositories;

public sealed class JobProcessingQueueRepository : IJobProcessingQueueRepository
{
    private readonly IDbConnectionFactory _connections;

    public JobProcessingQueueRepository(IDbConnectionFactory connections)
    {
        _connections = connections;
    }

    public bool Enqueue(int entityId, string taskType)
    {
        using var conn = _connections.Open();
        // INSERT OR IGNORE relies on the UNIQUE(entity_id, task_type) constraint. The row
        // counts as "new" only when the constraint didn't reject it. Avoids the read-then-
        // write race that two refresh threads would otherwise risk.
        var rows = conn.Execute(@"
            INSERT OR IGNORE INTO job_processing_queue (entity_id, task_type, status, enqueued_at)
            VALUES (@entityId, @taskType, 'pending', @now)",
            new { entityId, taskType, now = DateTime.UtcNow.ToString("o") });
        return rows > 0;
    }

    public IReadOnlyList<DequeuedItem> ClaimNext(string taskType, int max)
    {
        // The dequeue is two statements: SELECT candidate ids, then UPDATE to running. SQLite's
        // serialized writes inside a transaction prevent another worker from claiming the same
        // ids — but only if both statements are in the same connection + tx. We keep the
        // selection narrow (status = pending AND task_type matches) and bump attempts so a row
        // that keeps failing eventually becomes ineligible (worker checks attempts < maxAttempts).
        using var conn = _connections.Open();
        using var tx = conn.BeginTransaction();
        try
        {
            var candidates = conn.Query<(long Id, int EntityId, int Attempts)>(@"
                SELECT id AS Id, entity_id AS EntityId, attempts AS Attempts
                FROM job_processing_queue
                WHERE task_type = @taskType AND status = 'pending'
                ORDER BY enqueued_at
                LIMIT @max", new { taskType, max }, tx).ToList();
            if (candidates.Count == 0) { tx.Commit(); return Array.Empty<DequeuedItem>(); }

            var ids = candidates.Select(c => c.Id).ToList();
            conn.Execute(@"
                UPDATE job_processing_queue
                SET status = 'running', started_at = @now, attempts = attempts + 1
                WHERE id IN @ids",
                new { ids, now = DateTime.UtcNow.ToString("o") }, tx);
            tx.Commit();
            return candidates
                .Select(c => new DequeuedItem { QueueId = c.Id, EntityId = c.EntityId, Attempts = c.Attempts + 1 })
                .ToList();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public void MarkCompleted(long queueId)
    {
        using var conn = _connections.Open();
        conn.Execute(@"
            UPDATE job_processing_queue
            SET status = 'completed', completed_at = @now, last_error = NULL
            WHERE id = @queueId",
            new { queueId, now = DateTime.UtcNow.ToString("o") });
    }

    public void MarkFailed(long queueId, string error, int maxAttempts)
    {
        // Failed rows transition based on attempts vs cap. Past the cap → terminal 'failed'.
        // Below the cap → return to 'pending' for the next dequeue cycle. The row's attempts
        // counter was already incremented by ClaimNext, so we compare directly.
        using var conn = _connections.Open();
        conn.Execute(@"
            UPDATE job_processing_queue
            SET status      = CASE WHEN attempts >= @maxAttempts THEN 'failed' ELSE 'pending' END,
                last_error  = @error,
                completed_at = CASE WHEN attempts >= @maxAttempts THEN @now ELSE NULL END,
                started_at  = NULL
            WHERE id = @queueId",
            new { queueId, error = Truncate(error, 1000), maxAttempts, now = DateTime.UtcNow.ToString("o") });
    }

    public int ResetStaleRunning()
    {
        // No timestamp filter — any row in 'running' at app startup is by definition orphaned
        // because the workers don't start running until *after* this sweep. attempts is left
        // alone; the previous half-attempt already counted, the next try gets the bump.
        using var conn = _connections.Open();
        return conn.Execute(@"
            UPDATE job_processing_queue
            SET status = 'pending', started_at = NULL
            WHERE status = 'running'");
    }

    public IReadOnlyList<QueueStatRow> GetStats()
    {
        using var conn = _connections.Open();
        var rows = conn.Query<(string TaskType, string Status, int Count)>(@"
            SELECT task_type AS TaskType, status AS Status, COUNT(*) AS Count
            FROM job_processing_queue
            GROUP BY task_type, status
            ORDER BY task_type, status").ToList();
        return rows
            .Select(r => new QueueStatRow { TaskType = r.TaskType, Status = r.Status, Count = r.Count })
            .ToList();
    }

    public int EnqueueMissing(IEnumerable<int> entityIds, string taskType)
    {
        // One INSERT-OR-IGNORE per id inside a transaction. Cheaper than a single statement with
        // VALUES (...) (...) (...) when the id list is huge, because we don't have to escape
        // anything; Dapper handles parameter binding row-by-row, and the constraint absorbs dupes.
        var ids = entityIds.ToList();
        if (ids.Count == 0) return 0;
        using var conn = _connections.Open();
        using var tx = conn.BeginTransaction();
        var inserted = 0;
        var now = DateTime.UtcNow.ToString("o");
        foreach (var id in ids)
        {
            inserted += conn.Execute(@"
                INSERT OR IGNORE INTO job_processing_queue (entity_id, task_type, status, enqueued_at)
                VALUES (@id, @taskType, 'pending', @now)",
                new { id, taskType, now }, tx);
        }
        tx.Commit();
        return inserted;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max);
}
