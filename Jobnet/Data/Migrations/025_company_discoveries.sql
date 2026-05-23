-- 025: One row per time we saw a company in a source (Brave search result, BuiltIn Vancouver,
-- Yaletown portfolio, Hacker News Who Is Hiring, an aggregator board, AI competitor suggestion).
-- This is many-to-one against companies — one company can be discovered many times via
-- different sources, and we want to record every sighting.

CREATE TABLE company_discoveries (
    id              INTEGER PRIMARY KEY,
    company_id      INTEGER NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
    source_type     TEXT NOT NULL,     -- 'brave_search', 'directory', 'board', 'ai_competitor', 'seed_csv', 'manual'
    source_name     TEXT NOT NULL,     -- e.g. 'BuiltIn Vancouver companies', 'Vancouver fintech company' (the search term), 'Trulioo' (the seed for AI competitor)
    source_url      TEXT,              -- the harvested URL when applicable
    run_id          INTEGER REFERENCES run_log(id) ON DELETE SET NULL,
    discovered_at   TEXT NOT NULL
);

CREATE INDEX idx_company_discoveries_company  ON company_discoveries(company_id);
CREATE INDEX idx_company_discoveries_type     ON company_discoveries(source_type);
CREATE INDEX idx_company_discoveries_time     ON company_discoveries(discovered_at DESC);
