using System.Collections.Generic;

namespace Jobnet.Data.Repositories;

/// <summary>
/// Queue for asynchronous per-job processing tasks. The two callers today are
/// <c>SummaryWorker</c> and <c>ResumeMatchWorker</c>, both started at app launch. Tasks
/// are enqueued automatically by <see cref="IJobRepository"/> on new-job upsert; a CLI
/// (<c>queue-backfill</c>) seeds rows for jobs that existed before the queue did.
///
/// Task types live in <see cref="JobProcessingTaskTypes"/>. Status values in
/// <see cref="JobProcessingStatus"/>.
/// </summary>
public interface IJobProcessingQueueRepository
{
    /// <summary>Add a queue row for (entityId, taskType). <paramref name="entityId"/> is a
    /// jobs.id for task_types <c>summary</c>/<c>resume_match</c>, a companies.id for
    /// <c>company_profile</c>. Idempotent — the UNIQUE constraint silently absorbs duplicates
    /// so we can call this freely on every Upsert without checking first. Returns true when a
    /// new row was created, false when it already existed (in any status — including
    /// completed).</summary>
    bool Enqueue(int entityId, string taskType);

    /// <summary>Pull up to <paramref name="max"/> pending rows for the given task type and
    /// atomically flip them to 'running'. Returns the queue ids + entity ids of what was
    /// dequeued. Atomic so two workers of the same type can't claim the same row.</summary>
    IReadOnlyList<DequeuedItem> ClaimNext(string taskType, int max);

    /// <summary>Mark a claimed row as successfully completed.</summary>
    void MarkCompleted(long queueId);

    /// <summary>Mark a claimed row as failed. <paramref name="error"/> is persisted to
    /// <c>last_error</c>; the row stays in 'failed' if attempts &gt;= max-attempts threshold,
    /// otherwise it returns to 'pending' for retry.</summary>
    void MarkFailed(long queueId, string error, int maxAttempts);

    /// <summary>Counts grouped by (task_type, status). Used by the queue-stats CLI / UI.</summary>
    IReadOnlyList<QueueStatRow> GetStats();

    /// <summary>Sweep rows stuck in <c>running</c> from a previous app session and put them
    /// back to <c>pending</c>. Workers don't unwind their queue state when the process is
    /// killed mid-cycle, so without this orphans accumulate. Called once at app boot.
    /// Returns the count of rows reset.</summary>
    int ResetStaleRunning();

    /// <summary>Bulk-enqueue: for every entity_id in <paramref name="entityIds"/> that doesn't
    /// already have a row for <paramref name="taskType"/>, insert one. Returns the count
    /// actually inserted. Used by queue-backfill to seed the queue for existing entities.</summary>
    int EnqueueMissing(System.Collections.Generic.IEnumerable<int> entityIds, string taskType);
}

public static class JobProcessingTaskTypes
{
    public const string Summary         = "summary";
    public const string ResumeMatch     = "resume_match";
    public const string CompanyProfile  = "company_profile";
}

public static class JobProcessingStatus
{
    public const string Pending    = "pending";
    public const string Running    = "running";
    public const string Completed  = "completed";
    public const string Failed     = "failed";
}

public sealed class DequeuedItem
{
    public required long QueueId { get; init; }
    /// <summary>The entity id — a jobs.id for summary/resume_match tasks, a companies.id
    /// for company_profile. Interpretation is the worker's responsibility (it always knows
    /// which entity type its task_type refers to).</summary>
    public required int EntityId { get; init; }
    public required int Attempts { get; init; }
}

public sealed class QueueStatRow
{
    public required string TaskType { get; init; }
    public required string Status { get; init; }
    public required int Count { get; init; }
}
