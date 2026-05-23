-- 022: Track when the user has applied to a job. Null = not applied.
ALTER TABLE jobs ADD COLUMN applied_at TEXT;

CREATE INDEX IF NOT EXISTS idx_jobs_applied
    ON jobs(applied_at) WHERE is_active = 1;
