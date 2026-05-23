-- 024: General-purpose run log with per-step breakdown. Replaces / extends scan_log
-- (which only tracked refresh-jobs). Used for: discover_companies, refresh_jobs,
-- refresh_existing, resume_match, reclassify, prune_out_of_area, harvest_directory, etc.

CREATE TABLE run_log (
    id              INTEGER PRIMARY KEY,
    run_type        TEXT NOT NULL,
    scope           TEXT,
    started_at      TEXT NOT NULL,
    finished_at     TEXT,
    duration_ms     INTEGER,
    status          TEXT NOT NULL,     -- running | completed | partial | failed | cancelled
    items_examined  INTEGER NOT NULL DEFAULT 0,
    items_added     INTEGER NOT NULL DEFAULT 0,
    items_updated   INTEGER NOT NULL DEFAULT 0,
    items_skipped   INTEGER NOT NULL DEFAULT 0,
    items_failed    INTEGER NOT NULL DEFAULT 0,
    error_count     INTEGER NOT NULL DEFAULT 0,
    notes           TEXT
);

CREATE INDEX idx_run_log_started ON run_log(started_at DESC);
CREATE INDEX idx_run_log_type    ON run_log(run_type, started_at DESC);

CREATE TABLE run_step_log (
    id              INTEGER PRIMARY KEY,
    run_id          INTEGER NOT NULL REFERENCES run_log(id) ON DELETE CASCADE,
    step_name       TEXT NOT NULL,
    started_at      TEXT NOT NULL,
    finished_at     TEXT,
    duration_ms     INTEGER,
    status          TEXT NOT NULL,
    items_examined  INTEGER NOT NULL DEFAULT 0,
    items_added     INTEGER NOT NULL DEFAULT 0,
    items_updated   INTEGER NOT NULL DEFAULT 0,
    items_skipped   INTEGER NOT NULL DEFAULT 0,
    items_failed    INTEGER NOT NULL DEFAULT 0,
    error_message   TEXT
);

CREATE INDEX idx_run_step_log_run ON run_step_log(run_id);
