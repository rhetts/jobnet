-- 049: Generalise job_processing_queue to per-entity (job OR company OR future types).
--
-- Why: company-profile generation is conceptually identical to summary generation but the
-- subject is a company, not a job. The old schema baked "job_id" into the column name, which
-- forced us to either misuse the column for company ids (bad) or add a parallel table (worse).
-- Rename to entity_id; readers know which entity by task_type:
--   task_type='summary'           → entity_id is a jobs.id
--   task_type='resume_match'      → entity_id is a jobs.id
--   task_type='company_profile'   → entity_id is a companies.id  (new)
--
-- SQLite supports ALTER TABLE RENAME COLUMN since 3.25 (we ship newer). The unique constraint
-- on (job_id, task_type) still applies semantically — the index name and the column inside the
-- UNIQUE definition need updating too.
--
-- Approach: rename via table rebuild because SQLite can't ALTER a UNIQUE constraint in place
-- and we want to keep the constraint correct. The dataset is small (a few thousand rows max)
-- so this is fast.

PRAGMA foreign_keys = OFF;

CREATE TABLE _new_job_processing_queue (
    id            INTEGER PRIMARY KEY,
    entity_id     INTEGER NOT NULL,
    task_type     TEXT NOT NULL,
    status        TEXT NOT NULL DEFAULT 'pending',
    enqueued_at   TEXT NOT NULL,
    started_at    TEXT,
    completed_at  TEXT,
    attempts      INTEGER NOT NULL DEFAULT 0,
    last_error    TEXT,
    UNIQUE(entity_id, task_type)
);

INSERT INTO _new_job_processing_queue
    (id, entity_id, task_type, status, enqueued_at, started_at, completed_at, attempts, last_error)
SELECT id, job_id, task_type, status, enqueued_at, started_at, completed_at, attempts, last_error
FROM job_processing_queue;

DROP TABLE job_processing_queue;
ALTER TABLE _new_job_processing_queue RENAME TO job_processing_queue;

-- Recreate the dequeue index on the new column name.
CREATE INDEX idx_queue_dequeue ON job_processing_queue(task_type, status, enqueued_at)
    WHERE status = 'pending';
CREATE INDEX idx_queue_status ON job_processing_queue(status);

PRAGMA foreign_keys = ON;

-- Company-profile worker tunables. Slower poll than summary because each profile fetch hits
-- the company's website (HTTP) + an AI call, and most users add companies sparsely.
INSERT OR REPLACE INTO config (key, value) VALUES
    ('worker.company_profile.enabled',          'true'),
    ('worker.company_profile.poll_seconds',     '60'),
    ('worker.company_profile.batch_size',       '3');
