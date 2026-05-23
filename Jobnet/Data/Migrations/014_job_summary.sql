-- 014: AI-generated short paragraph summary of what the job involves.
-- Populated lazily by JobSummarizer; null for jobs that haven't been processed yet.
ALTER TABLE jobs ADD COLUMN summary TEXT;
ALTER TABLE jobs ADD COLUMN summary_generated_at TEXT;
ALTER TABLE jobs ADD COLUMN summary_model TEXT;
