-- 017: Named filter presets — keyword + selected level ids + area ids + city names + show toggles.
-- Lets the user recall a previously-configured filter (e.g. "Senior Vancouver SWE",
-- "Marketing roles", "Remote-friendly").
CREATE TABLE saved_filters (
    id            INTEGER PRIMARY KEY,
    name          TEXT NOT NULL UNIQUE,
    payload       TEXT NOT NULL,     -- JSON-encoded FilterStateSnapshot
    date_created  TEXT NOT NULL,
    date_used     TEXT
);

CREATE INDEX idx_saved_filters_name ON saved_filters(name);
