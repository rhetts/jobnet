-- Levels and areas as first-class tables. Jobs map to exactly one level and 0..N areas.
-- See REQUIREMENTS.md §2.10. Replaces free-text area_category / level_category columns.

CREATE TABLE levels (
    id          INTEGER PRIMARY KEY,
    name        TEXT NOT NULL UNIQUE,
    sort_order  INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE areas (
    id          INTEGER PRIMARY KEY,
    name        TEXT NOT NULL UNIQUE,
    sort_order  INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE job_areas (
    job_id  INTEGER NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
    area_id INTEGER NOT NULL REFERENCES areas(id) ON DELETE CASCADE,
    PRIMARY KEY (job_id, area_id)
);

INSERT INTO levels (name, sort_order) VALUES
    ('Senior',            0),
    ('Staff / Principal', 1),
    ('Lead',              2),
    ('Mid',               3),
    ('Manager',           4),
    ('Director',          5),
    ('Junior',            6),
    ('VP+',               7),
    ('Unknown',           8);

INSERT INTO areas (name, sort_order) VALUES
    ('Software Engineering', 0),
    ('Data / ML',            1),
    ('DevOps / Platform',    2),
    ('Security',             3),
    ('QA / Test',            4),
    ('Product Management',   5),
    ('Design',               6),
    ('Management',           7),
    ('Other',                8);

-- Add level_id FK to jobs. Drop the old text-based scoring index and columns.
DROP INDEX IF EXISTS idx_jobs_area_level;
ALTER TABLE jobs DROP COLUMN area_category;
ALTER TABLE jobs DROP COLUMN level_category;
ALTER TABLE jobs ADD COLUMN level_id INTEGER REFERENCES levels(id) ON DELETE SET NULL;

CREATE INDEX idx_jobs_level    ON jobs(level_id) WHERE is_active = 1;
CREATE INDEX idx_job_areas_aid ON job_areas(area_id);

-- These priority lists are now derived from the levels/areas tables' sort_order.
DELETE FROM config WHERE key IN ('area_priorities', 'level_priorities');
