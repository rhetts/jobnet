-- 042: Track AI-derived parser-profile state per company. Lets the new selector-replayer
-- branch in JobRefresher know whether the cached profile is healthy, drifted (returned 0
-- jobs when the company previously had some), or errored (selector eval threw / produced
-- garbage). Also lets the user manually disable the selector path for a problem company
-- and force AI extraction every time.
--
-- The profile JSON itself lives in the existing companies.parser_strategy column (added
-- in 001) which has been declared-but-unused until now.

ALTER TABLE companies ADD COLUMN parser_strategy_disabled INTEGER NOT NULL DEFAULT 0;
ALTER TABLE companies ADD COLUMN parser_strategy_derived_at TEXT;
ALTER TABLE companies ADD COLUMN parser_strategy_last_result TEXT;
ALTER TABLE companies ADD COLUMN parser_strategy_last_result_at TEXT;
ALTER TABLE companies ADD COLUMN parser_strategy_last_error TEXT;

-- Small index to power the Parser Report screen's filter-by-status view.
CREATE INDEX idx_companies_parser_result ON companies(parser_strategy_last_result);
