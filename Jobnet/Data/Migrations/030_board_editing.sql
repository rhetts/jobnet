-- 030: Make aggregator_sources (Boards) editable like discovery_seeds (Directories) —
-- add a max_pages column so boards can be paginated too. Pre-existing rows default to 1.
ALTER TABLE aggregator_sources ADD COLUMN max_pages INTEGER NOT NULL DEFAULT 1;
