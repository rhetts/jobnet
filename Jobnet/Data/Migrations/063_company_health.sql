-- 063: Company health -- a visible "this company's config is broken" flag. Before this, a dead
-- board or site only left a line in companies.notes and an entry in the run's error list, and
-- timeouts / DNS failures / moved sites weren't tracked at all.
--   health_status -- NULL = fine. Otherwise one of: board_gone | board_empty | fetch_failing |
--                    site_dead | site_moved | no_careers_page. Set by refresh and ATS detection,
--                    cleared by the next refresh that returns jobs (or a detection that finds an ATS).
--   health_reason -- human-readable detail, shown as the sidebar tooltip.
--   health_since  -- when the current status was first seen (kept while it stays the same).
ALTER TABLE companies ADD COLUMN health_status TEXT;
ALTER TABLE companies ADD COLUMN health_reason TEXT;
ALTER TABLE companies ADD COLUMN health_since TEXT;
