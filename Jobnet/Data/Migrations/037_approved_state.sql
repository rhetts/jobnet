-- 037: The C# enum `Interesting` was renamed to `Approved` and now drives which tab a job
-- lives in on the main window. We DON'T touch the DB text — the jobs/companies tables have
-- a CHECK constraint pinned to 'interesting' / 'not_interesting' from migration 001, and
-- relaxing that here would require a full table rebuild. Instead the repository layer
-- (JobRepository / CompanyRepository ParseInterest) maps the legacy 'interesting' string
-- to InterestLevel.Approved on read, and continues to WRITE 'interesting' for Approved jobs
-- so existing rows stay valid.
--
-- This migration is intentionally a no-op marker so the version counter advances.
SELECT 1;
