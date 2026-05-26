-- 039: Backfill company_discoveries for legacy companies that were inserted before
-- provenance tracking covered every code path (manual CLI, seed CSV, early UI adds).
-- We can't know the true origin retroactively, so we write a marker row with
-- source_type='unknown', source_name='(pre-tracking)' and use the company's
-- date_discovered as the timestamp so the timeline stays honest.

INSERT INTO company_discoveries (company_id, source_type, source_name, source_url, run_id, discovered_at)
SELECT c.id,
       'unknown',
       '(pre-tracking)',
       NULL,
       NULL,
       COALESCE(c.date_discovered, datetime('now'))
FROM companies c
WHERE NOT EXISTS (
    SELECT 1 FROM company_discoveries d WHERE d.company_id = c.id
);
