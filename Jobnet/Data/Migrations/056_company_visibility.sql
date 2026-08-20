-- 056: Per-company "hide from results" checkbox in the sidebar.
--
-- Distinct from is_active (stops scanning only, company keeps showing historical jobs) and
-- is_blacklisted (stops scanning AND removes the company from the sidebar entirely). Hidden
-- companies stay in the sidebar — checked by default — so unchecking is reversible with one
-- click: their jobs just drop out of the jobs list until re-checked.
--
-- Default 1 (visible) so existing rows are unaffected.

ALTER TABLE companies ADD COLUMN is_visible INTEGER NOT NULL DEFAULT 1 CHECK (is_visible IN (0,1));

CREATE INDEX idx_companies_hidden ON companies(is_visible) WHERE is_visible = 0;
