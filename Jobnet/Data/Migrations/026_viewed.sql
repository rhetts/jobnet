-- 026: Per-job "viewed" flag. Unlike applied/archived, viewed jobs still show in the
-- list — they're just visually dimmed so the user can see what's already been looked at.
ALTER TABLE jobs ADD COLUMN viewed_at TEXT;

CREATE INDEX IF NOT EXISTS idx_jobs_viewed
    ON jobs(viewed_at) WHERE is_active = 1;
