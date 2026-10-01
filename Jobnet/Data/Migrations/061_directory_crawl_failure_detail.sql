-- 061: Say *why* a directory page failed. Every failed crawl used to be recorded with the same
-- literal "fetch or AI failed", so a flaky directory couldn't be told apart from a broken parser
-- or an AI outage without re-running it.
--   failure_stage — which step broke: fetch | custom_parser | ai_call | exception.
--                   custom_parser can appear on a success row (the parser threw, the AI fallback
--                   still produced candidates) — that's a template change worth fixing.
--   source_name   — the seed/strategy name, so rows can be grouped per directory, not per URL.
ALTER TABLE directory_crawls ADD COLUMN failure_stage TEXT;
ALTER TABLE directory_crawls ADD COLUMN source_name TEXT;
CREATE INDEX idx_directory_crawls_failures ON directory_crawls(failure_stage, fetched_at DESC)
    WHERE failure_stage IS NOT NULL;
