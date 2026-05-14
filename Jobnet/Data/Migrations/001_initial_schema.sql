-- Jobnet initial schema (see REQUIREMENTS.md §5)
-- All dates stored as UTC ISO-8601 TEXT. Displayed in local time in the UI.

CREATE TABLE companies (
    id              INTEGER PRIMARY KEY,
    name            TEXT NOT NULL,
    domain          TEXT NOT NULL UNIQUE,
    website_url     TEXT,
    careers_url     TEXT,
    ats_type        TEXT,
    ats_slug        TEXT,
    parser_strategy TEXT,
    industry_tags   TEXT,
    city            TEXT,
    interest_level  TEXT DEFAULT NULL
                    CHECK (interest_level IN ('interesting','not_interesting') OR interest_level IS NULL),
    notes           TEXT,
    date_discovered TEXT NOT NULL,
    date_last_scan  TEXT,
    is_active       INTEGER DEFAULT 1
);

CREATE TABLE jobs (
    id                  INTEGER PRIMARY KEY,
    company_id          INTEGER NOT NULL REFERENCES companies(id),
    hash                TEXT NOT NULL UNIQUE,
    hash_tier           INTEGER NOT NULL,
    title               TEXT NOT NULL,
    url                 TEXT,
    location            TEXT,
    remote_type         TEXT CHECK (remote_type IN ('on-site','hybrid','remote','unknown') OR remote_type IS NULL),
    employment_type     TEXT CHECK (employment_type IN ('full-time','part-time','contract','unknown') OR employment_type IS NULL),
    area_category       TEXT,
    level_category      TEXT,
    description_snippet TEXT,
    salary_range        TEXT,
    source              TEXT,
    interest_level      TEXT DEFAULT NULL
                        CHECK (interest_level IN ('interesting','not_interesting') OR interest_level IS NULL),
    notes               TEXT,
    extraction_version  TEXT,
    date_first_seen     TEXT NOT NULL,
    date_last_seen      TEXT NOT NULL,
    date_removed        TEXT,
    is_active           INTEGER DEFAULT 1 CHECK (is_active IN (0, 1))
);

CREATE TABLE search_terms (
    id          INTEGER PRIMARY KEY,
    term        TEXT NOT NULL,
    type        TEXT NOT NULL CHECK (type IN ('company_discovery','job_search')),
    is_active   INTEGER DEFAULT 1,
    date_added  TEXT NOT NULL
);

CREATE TABLE aggregator_sources (
    id                  INTEGER PRIMARY KEY,
    name                TEXT NOT NULL,
    base_url            TEXT NOT NULL,
    search_url_template TEXT,
    is_enabled          INTEGER DEFAULT 0,
    notes               TEXT
);

CREATE TABLE page_fetches (
    id                  INTEGER PRIMARY KEY,
    company_id          INTEGER REFERENCES companies(id),
    url                 TEXT NOT NULL,
    fetched_at          TEXT NOT NULL,
    http_status         INTEGER,
    content_sha256      TEXT,
    parser_strategy     TEXT,
    claude_tokens_in    INTEGER,
    claude_tokens_out   INTEGER,
    extraction_json     TEXT,
    success             INTEGER CHECK (success IN (0, 1)),
    error_message       TEXT
);

CREATE TABLE config (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE scan_log (
    id            INTEGER PRIMARY KEY,
    scan_time     TEXT NOT NULL,
    scan_type     TEXT,
    scope         TEXT,
    status        TEXT,
    companies_hit INTEGER,
    jobs_found    INTEGER,
    jobs_added    INTEGER,
    jobs_removed  INTEGER,
    errors        TEXT
);

CREATE INDEX idx_jobs_company_active ON jobs(company_id, is_active);
CREATE INDEX idx_jobs_area_level     ON jobs(area_category, level_category) WHERE is_active = 1;
CREATE INDEX idx_jobs_first_seen     ON jobs(date_first_seen DESC);
CREATE INDEX idx_jobs_hash           ON jobs(hash);
CREATE INDEX idx_page_fetches_sha    ON page_fetches(content_sha256);
