-- 043: Record which hand-written ICompanyParser most recently extracted jobs for a company.
-- Null when the company has never matched a hand-written parser (native ATS / selectors /
-- AI extract / never scanned). Set by JobRefresher's AI-extract path after a positive
-- CompanyParserRegistry probe.
--
-- This complements parser_strategy (the AI-derived selector profile). The selector path is
-- generic / one-per-site; the company_parser path is a hand-coded pattern that may cover
-- many companies sharing a template (e.g. the WordPress lever shortcode plugin).

ALTER TABLE companies ADD COLUMN last_company_parser TEXT;

CREATE INDEX idx_companies_last_company_parser ON companies(last_company_parser)
    WHERE last_company_parser IS NOT NULL;
