-- 062: ATS "site:" search terms for company discovery.
--
-- Why: the generic company_discovery terms ("Vancouver SaaS") return company homepages, and every
-- one of those then needs slow ATS detection before we can pull jobs. A query like
-- `site:jobs.ashbyhq.com Vancouver` returns job-board URLs that already embed the ATS and the
-- account slug (jobs.ashbyhq.com/klue), so DiscoveryService can set ats_type/ats_slug at insert
-- time and skip detection entirely.
--
-- These get their own type, 'ats_site', so DiscoveryService can run them first and cap them at
-- one page each (Brave's soft cap is ~60 calls/day). The original CHECK constraint only allowed
-- 'company_discovery' and 'job_search', and SQLite can't ALTER a CHECK in place, so the table is
-- rebuilt. Nothing references search_terms, and it's ~30 rows.

CREATE TABLE _new_search_terms (
    id          INTEGER PRIMARY KEY,
    term        TEXT NOT NULL,
    type        TEXT NOT NULL CHECK (type IN ('company_discovery','job_search','ats_site')),
    is_active   INTEGER DEFAULT 1,
    date_added  TEXT NOT NULL
);

INSERT INTO _new_search_terms (id, term, type, is_active, date_added)
SELECT id, term, type, is_active, date_added FROM search_terms;

DROP TABLE search_terms;
ALTER TABLE _new_search_terms RENAME TO search_terms;

-- One host per ATS we have an adapter for, using the hosts AtsDetector.UrlPatterns recognises
-- (a hit on any other host can't be turned into ats_type/ats_slug). Kept to plain `site:` +
-- location words -- Brave's boolean operator support is unreliable. BambooHR and Cornerstone are
-- left out: search engines barely index their job pages, and their boards never show the
-- company website, so a hit couldn't be resolved to a company anyway.
INSERT OR IGNORE INTO search_terms (term, type, is_active, date_added) VALUES
    ('site:jobs.ashbyhq.com Vancouver',                    'ats_site', 1, datetime('now')),
    ('site:jobs.ashbyhq.com British Columbia',             'ats_site', 1, datetime('now')),
    ('site:job-boards.greenhouse.io Vancouver',            'ats_site', 1, datetime('now')),
    ('site:job-boards.greenhouse.io British Columbia',     'ats_site', 1, datetime('now')),
    ('site:boards.greenhouse.io Vancouver',                'ats_site', 1, datetime('now')),
    ('site:jobs.lever.co Vancouver',                       'ats_site', 1, datetime('now')),
    ('site:jobs.lever.co British Columbia',                'ats_site', 1, datetime('now')),
    ('site:apply.workable.com Vancouver',                  'ats_site', 1, datetime('now')),
    ('site:apply.workable.com British Columbia',           'ats_site', 1, datetime('now')),
    ('site:careers.smartrecruiters.com Vancouver',         'ats_site', 1, datetime('now')),
    ('site:careers.smartrecruiters.com British Columbia',  'ats_site', 1, datetime('now')),
    ('site:myworkdayjobs.com Vancouver',                   'ats_site', 1, datetime('now')),
    ('site:myworkdayjobs.com British Columbia',            'ats_site', 1, datetime('now'));
