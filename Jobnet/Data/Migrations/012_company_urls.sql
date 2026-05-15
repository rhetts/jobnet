-- Cache of useful URLs discovered per company. Lets future refreshes skip Playwright
-- rediscovery and hit known endpoints directly. See REQUIREMENTS.md §2.13 (Phase 7.5).

CREATE TABLE company_urls (
    id                INTEGER PRIMARY KEY,
    company_id        INTEGER NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
    url               TEXT NOT NULL,
    kind              TEXT NOT NULL,    -- 'careers_root' | 'department' | 'job_list' | 'ats_api' | 'job_detail' | 'unknown'
    label             TEXT,             -- e.g. "Engineering" for a department, or human-readable hint
    discovered_via    TEXT,             -- 'network_listener' | 'anchor_scan' | 'redirect' | 'manual' | 'ats_detection'
    fail_count        INTEGER DEFAULT 0,
    last_seen         TEXT NOT NULL,    -- ISO-8601 UTC, last time we successfully reached this URL
    last_yielded      TEXT,             -- last time this URL contributed ≥1 job, or NULL
    UNIQUE(company_id, url)
);

CREATE INDEX idx_company_urls_company ON company_urls(company_id, kind);
CREATE INDEX idx_company_urls_kind    ON company_urls(kind);
