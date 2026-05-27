-- 041: Distinguish recruitment agencies from direct employers. Agency postings have
-- different semantics: same role is often posted by multiple agencies (dedup confusion),
-- the actual employer is hidden until interview, contract roles dominate. The flag lets
-- the UI badge them and the user filter them out when they want direct-employer only.

ALTER TABLE companies ADD COLUMN is_agency INTEGER NOT NULL DEFAULT 0;

CREATE INDEX idx_companies_is_agency ON companies(is_agency) WHERE is_agency = 1;
