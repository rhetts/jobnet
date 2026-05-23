using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using Jobnet.Data;

namespace Jobnet.Services.Logging;

public sealed class RunLogger : IRunLogger
{
    private readonly IDbConnectionFactory _connections;

    public RunLogger(IDbConnectionFactory connections)
    {
        _connections = connections;
    }

    public long StartRun(string runType, string? scope = null)
    {
        using var conn = _connections.Open();
        return conn.ExecuteScalar<long>(@"
            INSERT INTO run_log (run_type, scope, started_at, status)
            VALUES (@runType, @scope, @startedAt, 'running');
            SELECT last_insert_rowid();",
            new { runType, scope, startedAt = DateTime.UtcNow.ToString("o") });
    }

    public long StartStep(long runId, string stepName)
    {
        using var conn = _connections.Open();
        return conn.ExecuteScalar<long>(@"
            INSERT INTO run_step_log (run_id, step_name, started_at, status)
            VALUES (@runId, @stepName, @startedAt, 'running');
            SELECT last_insert_rowid();",
            new { runId, stepName, startedAt = DateTime.UtcNow.ToString("o") });
    }

    public void FinishStep(long stepId, string status,
                            int examined = 0, int added = 0, int updated = 0,
                            int skipped = 0, int failed = 0, string? errorMessage = null)
    {
        using var conn = _connections.Open();
        var startedAtIso = conn.ExecuteScalar<string>(
            "SELECT started_at FROM run_step_log WHERE id = @stepId", new { stepId });
        var finishedAt = DateTime.UtcNow;
        int? durationMs = null;
        if (DateTime.TryParse(startedAtIso, out var s)) durationMs = (int)(finishedAt - s.ToUniversalTime()).TotalMilliseconds;

        conn.Execute(@"
            UPDATE run_step_log
            SET finished_at = @finishedAt, duration_ms = @durationMs, status = @status,
                items_examined = @examined, items_added = @added, items_updated = @updated,
                items_skipped = @skipped, items_failed = @failed,
                error_message = @errorMessage
            WHERE id = @stepId",
            new { stepId, finishedAt = finishedAt.ToString("o"), durationMs, status,
                  examined, added, updated, skipped, failed, errorMessage });
    }

    public void FinishRun(long runId, string status,
                           int examined = 0, int added = 0, int updated = 0,
                           int skipped = 0, int failed = 0, int errorCount = 0,
                           string? notes = null)
    {
        using var conn = _connections.Open();
        var startedAtIso = conn.ExecuteScalar<string>(
            "SELECT started_at FROM run_log WHERE id = @runId", new { runId });
        var finishedAt = DateTime.UtcNow;
        int? durationMs = null;
        if (DateTime.TryParse(startedAtIso, out var s)) durationMs = (int)(finishedAt - s.ToUniversalTime()).TotalMilliseconds;

        conn.Execute(@"
            UPDATE run_log
            SET finished_at = @finishedAt, duration_ms = @durationMs, status = @status,
                items_examined = @examined, items_added = @added, items_updated = @updated,
                items_skipped = @skipped, items_failed = @failed,
                error_count = @errorCount, notes = @notes
            WHERE id = @runId",
            new { runId, finishedAt = finishedAt.ToString("o"), durationMs, status,
                  examined, added, updated, skipped, failed, errorCount, notes });
    }

    public IReadOnlyList<RunSummary> GetRecent(int limit = 50)
    {
        using var conn = _connections.Open();
        return conn.Query<RunRow>(@"
            SELECT id, run_type AS RunType, scope, started_at AS StartedAt,
                   finished_at AS FinishedAt, duration_ms AS DurationMs, status,
                   items_examined AS Examined, items_added AS Added, items_updated AS Updated,
                   items_skipped AS Skipped, items_failed AS Failed,
                   error_count AS ErrorCount, notes
            FROM run_log ORDER BY started_at DESC LIMIT @limit",
            new { limit }).Select(MapRun).ToList();
    }

    public DateTime? GetLastRunStartedAt(string runType, string scopeContains)
    {
        using var conn = _connections.Open();
        var iso = conn.ExecuteScalar<string?>(@"
            SELECT started_at FROM run_log
            WHERE run_type = @runType AND scope LIKE @pat
            ORDER BY started_at DESC LIMIT 1",
            new { runType, pat = "%" + scopeContains + "%" });
        return string.IsNullOrEmpty(iso) ? null : DateTime.Parse(iso).ToUniversalTime();
    }

    public DateTime? GetLastRunStartedAt(string runType)
    {
        using var conn = _connections.Open();
        var iso = conn.ExecuteScalar<string?>(@"
            SELECT started_at FROM run_log
            WHERE run_type = @runType
            ORDER BY started_at DESC LIMIT 1",
            new { runType });
        return string.IsNullOrEmpty(iso) ? null : DateTime.Parse(iso).ToUniversalTime();
    }

    public int CleanupDanglingRuns()
    {
        using var conn = _connections.Open();
        using var tx = conn.BeginTransaction();

        // Backfill aggregate counts on dangling runs from their step rows so the history
        // page shows what was actually accomplished before the crash/kill. Steps that were
        // themselves still 'running' contribute nothing (their items_* columns stay 0).
        conn.Execute(@"
            UPDATE run_log
            SET items_examined = COALESCE((SELECT SUM(items_examined) FROM run_step_log WHERE run_id = run_log.id), items_examined),
                items_added    = COALESCE((SELECT SUM(items_added)    FROM run_step_log WHERE run_id = run_log.id), items_added),
                items_updated  = COALESCE((SELECT SUM(items_updated)  FROM run_step_log WHERE run_id = run_log.id), items_updated),
                items_skipped  = COALESCE((SELECT SUM(items_skipped)  FROM run_step_log WHERE run_id = run_log.id), items_skipped),
                items_failed   = COALESCE((SELECT SUM(items_failed)   FROM run_step_log WHERE run_id = run_log.id), items_failed),
                error_count    = COALESCE((SELECT COUNT(*) FROM run_step_log WHERE run_id = run_log.id AND error_message IS NOT NULL), error_count)
            WHERE status = 'running';", transaction: tx);

        var rows = conn.Execute(@"
            UPDATE run_log
            SET status = 'interrupted',
                finished_at = @now,
                notes = COALESCE(notes || ' | ', '') || 'auto-marked interrupted on next startup'
            WHERE status = 'running';",
            new { now = DateTime.UtcNow.ToString("o") }, transaction: tx);

        conn.Execute(@"
            UPDATE run_step_log
            SET status = 'interrupted', finished_at = @now
            WHERE status = 'running';",
            new { now = DateTime.UtcNow.ToString("o") }, transaction: tx);

        tx.Commit();
        return rows;
    }

    public IReadOnlyList<StepSummary> GetSteps(long runId)
    {
        using var conn = _connections.Open();
        return conn.Query<StepRow>(@"
            SELECT id, run_id AS RunId, step_name AS StepName, started_at AS StartedAt,
                   finished_at AS FinishedAt, duration_ms AS DurationMs, status,
                   items_examined AS Examined, items_added AS Added, items_updated AS Updated,
                   items_skipped AS Skipped, items_failed AS Failed,
                   error_message AS ErrorMessage
            FROM run_step_log WHERE run_id = @runId ORDER BY id",
            new { runId }).Select(MapStep).ToList();
    }

    private static RunSummary MapRun(RunRow r) => new()
    {
        Id = r.Id, RunType = r.RunType, Scope = r.Scope,
        StartedAt = DateTime.Parse(r.StartedAt).ToUniversalTime(),
        FinishedAt = string.IsNullOrEmpty(r.FinishedAt) ? null : DateTime.Parse(r.FinishedAt).ToUniversalTime(),
        DurationMs = r.DurationMs, Status = r.Status,
        Examined = r.Examined, Added = r.Added, Updated = r.Updated,
        Skipped = r.Skipped, Failed = r.Failed,
        ErrorCount = r.ErrorCount, Notes = r.Notes,
    };

    private static StepSummary MapStep(StepRow r) => new()
    {
        Id = r.Id, RunId = r.RunId, StepName = r.StepName,
        StartedAt = DateTime.Parse(r.StartedAt).ToUniversalTime(),
        FinishedAt = string.IsNullOrEmpty(r.FinishedAt) ? null : DateTime.Parse(r.FinishedAt).ToUniversalTime(),
        DurationMs = r.DurationMs, Status = r.Status,
        Examined = r.Examined, Added = r.Added, Updated = r.Updated,
        Skipped = r.Skipped, Failed = r.Failed,
        ErrorMessage = r.ErrorMessage,
    };

    private sealed class RunRow
    {
        public long Id { get; set; }
        public string RunType { get; set; } = "";
        public string? Scope { get; set; }
        public string StartedAt { get; set; } = "";
        public string? FinishedAt { get; set; }
        public int? DurationMs { get; set; }
        public string Status { get; set; } = "";
        public int Examined { get; set; }
        public int Added { get; set; }
        public int Updated { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public int ErrorCount { get; set; }
        public string? Notes { get; set; }
    }

    private sealed class StepRow
    {
        public long Id { get; set; }
        public long RunId { get; set; }
        public string StepName { get; set; } = "";
        public string StartedAt { get; set; } = "";
        public string? FinishedAt { get; set; }
        public int? DurationMs { get; set; }
        public string Status { get; set; } = "";
        public int Examined { get; set; }
        public int Added { get; set; }
        public int Updated { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
