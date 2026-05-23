-- 031: Per-URL crawl log so we can throttle/skip recently-fetched pages on subsequent runs.
-- The URL row already encodes the page number (e.g. ?page=5), so each (URL, fetched_at) is a
-- single page crawl.
CREATE TABLE directory_crawls (
    id                 INTEGER PRIMARY KEY,
    url                TEXT NOT NULL,
    fetched_at         TEXT NOT NULL,
    duration_ms        INTEGER,
    candidates_found   INTEGER NOT NULL DEFAULT 0,
    candidates_added   INTEGER NOT NULL DEFAULT 0,
    success            INTEGER NOT NULL DEFAULT 1,
    error_message      TEXT
);

CREATE INDEX idx_directory_crawls_url ON directory_crawls(url, fetched_at DESC);

-- Default skip threshold (days). 0 = always crawl, no skipping.
INSERT OR IGNORE INTO config (key, value) VALUES ('discovery_skip_days_threshold', '0');
