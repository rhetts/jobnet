-- 020: Per-job resume match score from Gemini. Updated by ResumeMatcher when the user
-- uploads a resume; cleared when a new resume replaces the old one.
ALTER TABLE jobs ADD COLUMN resume_match_score   INTEGER;
ALTER TABLE jobs ADD COLUMN resume_match_reason  TEXT;
ALTER TABLE jobs ADD COLUMN resume_match_at      TEXT;

CREATE INDEX IF NOT EXISTS idx_jobs_resume_score
    ON jobs(resume_match_score DESC) WHERE is_active = 1;
