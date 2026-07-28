-- 052: Per-company blacklist.
--
-- Blacklisted companies are excluded from BOTH:
--   * the refresh loop (no more job scraping for them)
--   * the main jobs list view (their jobs are hidden so the user doesn't see them)
-- Distinct from is_active=0 ("retired/acquired") — that state still shows historical jobs,
-- whereas the blacklist is "I never want to see this company again". Existing jobs stay in
-- the DB so unblacklisting brings them right back without re-scraping.
--
-- Default 0 so existing rows stay visible; the user opts a company in via the right-click
-- menu on a job card.

ALTER TABLE companies ADD COLUMN is_blacklisted INTEGER NOT NULL DEFAULT 0;

-- Filtering in queries: a partial index keeps reads fast since most companies are NOT
-- blacklisted (the column is highly skewed).
CREATE INDEX idx_companies_blacklisted ON companies(is_blacklisted) WHERE is_blacklisted = 1;
