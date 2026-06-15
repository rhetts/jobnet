-- 048: Async job-processing queue.
--
-- Replaces the manual "Generate AI Summaries" and "Match Resume" buttons with two
-- always-on background workers. Every newly-inserted job gets two queue rows (one per
-- task_type). Workers poll for pending rows, process them, mark complete/failed.
--
-- Schema choices:
-- * One table with task_type column, not one table per worker. Easier to monitor,
--   easier to add new task types (e.g. tech-extraction, classification reclassify) later.
-- * UNIQUE(job_id, task_type) so re-enqueueing the same job for the same task is a no-op —
--   matters for the Upsert-on-update path and for the queue-backfill CLI.
-- * No FK to jobs(id) because SQLite's FK enforcement is per-connection and we don't want
--   queue inserts to fail just because the job was deleted in another connection. Workers
--   handle missing-job gracefully by marking the queue row 'failed' with note.

CREATE TABLE job_processing_queue (
    id            INTEGER PRIMARY KEY,
    job_id        INTEGER NOT NULL,
    task_type     TEXT NOT NULL,
        -- 'summary' | 'resume_match'
    status        TEXT NOT NULL DEFAULT 'pending',
        -- 'pending' | 'running' | 'completed' | 'failed'
    enqueued_at   TEXT NOT NULL,
    started_at    TEXT,
    completed_at  TEXT,
    attempts      INTEGER NOT NULL DEFAULT 0,
    last_error    TEXT,
    UNIQUE(job_id, task_type)
);

-- Hot path: workers SELECT pending rows. Filter on task_type first (worker dequeues only
-- its own type), then on status, ordered by enqueued_at so we process oldest first.
CREATE INDEX idx_queue_dequeue ON job_processing_queue(task_type, status, enqueued_at)
    WHERE status = 'pending';

-- Diagnostics: dashboards group by task_type + status.
CREATE INDEX idx_queue_status ON job_processing_queue(status);

-- Worker tunables. Defaults sized for a single-user setup:
-- * poll interval — how often the worker checks for new work
-- * batch size    — how many rows a worker pulls in one cycle
-- * max attempts  — failed rows past this stop retrying
-- * worker enabled flags — workers can be disabled without unwiring DI
INSERT OR REPLACE INTO config (key, value) VALUES
    ('worker.summary.enabled',          'true'),
    ('worker.summary.poll_seconds',     '30'),
    ('worker.summary.batch_size',       '5'),
    ('worker.resume_match.enabled',     'true'),
    ('worker.resume_match.poll_seconds','45'),
    ('worker.resume_match.batch_size',  '10'),
    ('worker.max_attempts',             '3');
