-- Company profile columns. Populated by CompanyProfiler (Claude Haiku summarizes homepage + /about).
-- All free-form JSON / TEXT so the structure can evolve without further migrations.

ALTER TABLE companies ADD COLUMN profile_summary       TEXT;
ALTER TABLE companies ADD COLUMN profile_products      TEXT;   -- JSON array of strings
ALTER TABLE companies ADD COLUMN profile_industries    TEXT;   -- JSON array of strings
ALTER TABLE companies ADD COLUMN profile_tech_signals  TEXT;   -- JSON array of strings
ALTER TABLE companies ADD COLUMN profile_hq_hint       TEXT;
ALTER TABLE companies ADD COLUMN profile_size_hint     TEXT;
ALTER TABLE companies ADD COLUMN profile_generated_at  TEXT;   -- ISO-8601 UTC
ALTER TABLE companies ADD COLUMN profile_model         TEXT;   -- e.g. 'claude-haiku-4-5'
