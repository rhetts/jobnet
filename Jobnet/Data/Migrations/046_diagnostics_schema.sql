-- 046: Logging & diagnostics schema overhaul.
--
-- Closes the gaps that made "why did X fail?" unanswerable in run 51:
--   * refresh_attempt — one row per (run, company, stage) so we can replay the decision tree.
--   * jobs.source_stage — which extraction path produced each job, for parser-ROI reporting.
--   * run_step_log.outcome_kind — finer-grained classification than status alone.
--   * api_call_log.run_id / company_id — correlate API calls with the refresh that issued them.
--   * companies.consecutive_failures / last_success_at / last_refresh_jobs_count — health signals
--     that drive auto-clear of stale slugs and 0-yield re-detection.
--   * ai_extraction_decisions — forensic log of what the AI returned vs what we accepted, with
--     hallucination flags. The Vanedge harvest debacle would have left a clear trail here.

-- ── refresh_attempt: stage-level history ─────────────────────────────────────────
CREATE TABLE refresh_attempt (
    id              INTEGER PRIMARY KEY,
    run_id          INTEGER REFERENCES run_log(id) ON DELETE CASCADE,
    company_id      INTEGER REFERENCES companies(id),
    stage           TEXT NOT NULL,
        -- One of: ats_api | jsonld | hand_written | selectors | ai_extract |
        --        playwright_fetch | detect_ats | cached_url | dom_hash_skip
    stage_detail    TEXT,
        -- Stage-specific identifier — ats_type for ats_api, parser name for hand_written,
        -- url kind for cached_url, etc. Helps slice "how often did greenhouse fail" without
        -- joining back to companies.
    started_at      TEXT NOT NULL,
    duration_ms     INTEGER,
    result          TEXT NOT NULL,
        -- success | empty | http_4xx | http_5xx | timeout | parse_exception |
        -- hallucination_rejected | filtered_out | skipped | cache_hit
    http_status     INTEGER,
    jobs_yielded    INTEGER NOT NULL DEFAULT 0,
    error_message   TEXT
);
CREATE INDEX idx_refresh_attempt_run     ON refresh_attempt(run_id);
CREATE INDEX idx_refresh_attempt_company ON refresh_attempt(company_id, started_at DESC);
CREATE INDEX idx_refresh_attempt_stage   ON refresh_attempt(stage, result);

-- ── Per-job source attribution ────────────────────────────────────────────────────
ALTER TABLE jobs ADD COLUMN source_stage TEXT;
    -- e.g. 'ats_greenhouse', 'ats_lever', 'ai_extract', 'jsonld',
    -- 'hand_written:lever_shortcode', 'cached_selectors'.
    -- Used by parser-stats --candidates to identify AI-extract companies that should
    -- get a hand-written parser.
CREATE INDEX idx_jobs_source_stage ON jobs(source_stage) WHERE is_active = 1;

-- ── Step-level outcome kind ───────────────────────────────────────────────────────
ALTER TABLE run_step_log ADD COLUMN outcome_kind TEXT;
    -- fetch_4xx | fetch_5xx | fetch_timeout | fetch_blocked | api_returned_empty |
    -- all_jobs_location_filtered | all_jobs_department_filtered | parse_exception |
    -- ai_hallucination_rejected | cancelled_user | success | dom_unchanged_skip
CREATE INDEX idx_run_step_log_outcome ON run_step_log(outcome_kind);

-- ── Tie API calls to the run + company that issued them ───────────────────────────
ALTER TABLE api_call_log ADD COLUMN run_id     INTEGER;
ALTER TABLE api_call_log ADD COLUMN company_id INTEGER;
CREATE INDEX idx_api_call_log_run ON api_call_log(run_id) WHERE run_id IS NOT NULL;

-- ── Company health signals ────────────────────────────────────────────────────────
ALTER TABLE companies ADD COLUMN consecutive_failures   INTEGER NOT NULL DEFAULT 0;
ALTER TABLE companies ADD COLUMN last_success_at        TEXT;
ALTER TABLE companies ADD COLUMN last_refresh_jobs_count INTEGER;
    -- last_refresh_jobs_count drives 0-yield drift detection: if previous refresh yielded
    -- >0 and this one yielded 0, the slug is probably stale and we should re-detect.
CREATE INDEX idx_companies_health ON companies(consecutive_failures DESC) WHERE is_active = 1;

-- ── AI extraction decisions (forensic) ────────────────────────────────────────────
CREATE TABLE ai_extraction_decisions (
    id                     INTEGER PRIMARY KEY,
    run_id                 INTEGER REFERENCES run_log(id) ON DELETE CASCADE,
    company_id             INTEGER REFERENCES companies(id),
    source_url             TEXT,
    provider               TEXT,
        -- gemini | groq | claude | llama
    raw_jobs_count         INTEGER NOT NULL DEFAULT 0,
    accepted_count         INTEGER NOT NULL DEFAULT 0,
    citation_verified_count INTEGER NOT NULL DEFAULT 0,
    rejected_titles        TEXT,
        -- JSON array of rejected job titles for forensics.
    suspected_hallucination INTEGER NOT NULL DEFAULT 0,
        -- 1 when any of the heuristics tripped: titles not in source DOM, all jobs share URL, etc.
    decided_at             TEXT NOT NULL
);
CREATE INDEX idx_ai_decisions_company ON ai_extraction_decisions(company_id, decided_at DESC);
CREATE INDEX idx_ai_decisions_hallucination ON ai_extraction_decisions(suspected_hallucination)
    WHERE suspected_hallucination = 1;
