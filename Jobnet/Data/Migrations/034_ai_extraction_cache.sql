-- 034: Cache AI-extracted job lists per URL so we don't re-burn tokens on every Discover Jobs
-- run. Hot path is AiExtractedJobSource — one ~3K-token call per company without a native ATS.
-- Cache row is keyed by URL, validated against the content hash of the cleaned page text +
-- anchors that would be sent to the AI. Cache miss on hash mismatch (page changed) OR on TTL
-- expiry (default 168h = 7 days) so a stale cache can't pin us to obsolete jobs forever.

CREATE TABLE ai_extraction_cache (
    id            INTEGER PRIMARY KEY,
    url           TEXT NOT NULL UNIQUE,
    content_hash  TEXT NOT NULL,
    cached_at     TEXT NOT NULL,
    jobs_json     TEXT NOT NULL,
    hit_count     INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX idx_ai_extraction_cache_url ON ai_extraction_cache(url);

INSERT OR IGNORE INTO config (key, value) VALUES
    ('ai_extraction_cache_ttl_hours', '168');
