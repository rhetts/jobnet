-- 051: Company size categorisation.
--
-- One bucket per company derived from profile_size_hint when available. Values are constrained
-- to the closed set so the UI can show predictable chips and the filter dropdown is finite.
--
-- Buckets (employee count):
--   startup    1-50
--   growth     51-200
--   mid_size   201-1000
--   large      1000+
--
-- NULL = unknown (no size hint yet, or hint was unparseable). CompanySizeClassifier is
-- responsible for translating the freeform AI hint into one of these values; we accept NULL
-- so jobs/companies don't get blocked from inserting just because we can't classify yet.

ALTER TABLE companies ADD COLUMN size_category TEXT;

-- Lookups in the Sources screen filter by category.
CREATE INDEX idx_companies_size_category ON companies(size_category);
