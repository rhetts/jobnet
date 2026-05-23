-- 028: Allow paginated harvesting per seed URL. The harvester appends ?page=N (or replaces
-- an existing page= value) and stops early on empty or all-duplicate pages.
ALTER TABLE discovery_seeds ADD COLUMN max_pages INTEGER NOT NULL DEFAULT 1;

-- Set sensible defaults for the seeds that we know paginate:
UPDATE discovery_seeds SET max_pages = 25 WHERE url LIKE '%builtinvancouver.org/companies%';
UPDATE discovery_seeds SET max_pages = 5  WHERE url LIKE '%workatastartup.com/companies%';
