-- 047: Enable URL-level recency skip for AI-extract refreshes.
--
-- ai_extraction_url_skip_hours = 6 means: if AiFallbackJobSource has cached jobs for a URL
-- within the last 6 hours, return them WITHOUT re-running Playwright + AI. The configured
-- ai_extraction_cache_ttl_hours (default 168 = 7 days) is the OUTER limit; this is the
-- inner "we just looked at this, don't bother again" window.
--
-- Bias: this trades freshness for cost. A page that gains a new posting within the window
-- is invisible until the window rolls. For most companies in the AI-extract bucket that's
-- fine — they're slow-changing marketing pages, not active hourly boards.
--
-- Default of 6 hours = up to 4 refreshes per day per company can take the fast lane. Set to
-- 0 to disable globally. Higher values save more Gemini quota but lengthen the staleness
-- window for slow-AI-pipe companies.

INSERT OR REPLACE INTO config (key, value) VALUES
    ('ai_extraction_url_skip_hours', '6');
