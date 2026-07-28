-- 050: Cover letters persisted per job.
--
-- One row per job: generating a letter inserts/replaces, editing in the UI updates
-- letter_text. We key by job_id so re-opening the Cover Letter window for a job
-- the user already worked on rehydrates the previous text instead of starting blank.
--
-- model and generated_at remember the last AI-produced version so the UI can show
-- "Loaded saved letter (gemini-flash, 2026-06-15)". updated_at tracks the most recent
-- write of any kind (generate OR manual edit).
--
-- No FK to jobs(id) — same rationale as job_processing_queue: SQLite FK enforcement
-- is per-connection and we'd rather an orphan row than a failed insert.

CREATE TABLE cover_letters (
    job_id        INTEGER PRIMARY KEY,
    letter_text   TEXT NOT NULL,
    model         TEXT,
    generated_at  TEXT,
    updated_at    TEXT NOT NULL
);
