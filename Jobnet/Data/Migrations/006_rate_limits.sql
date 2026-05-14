-- Per-provider minimum delay between calls (rate limit). Enforced by RateLimiter.
-- Brave Search free tier documents 1 query/second; we add a margin.

INSERT OR IGNORE INTO config (key, value) VALUES
    ('api_min_delay_ms.brave_search',          '1100'),
    ('api_min_delay_ms.google_cse',             '500'),
    ('api_min_delay_ms.ats_greenhouse',         '300'),
    ('api_min_delay_ms.ats_lever',              '300'),
    ('api_min_delay_ms.ats_ashby',              '300'),
    ('api_min_delay_ms.ats_workable',           '300'),
    ('api_min_delay_ms.ats_smartrecruiters',    '300'),
    ('api_min_delay_ms.claude_haiku',           '100'),
    ('api_min_delay_ms.http_fetch',             '500');

-- Soft caps for fetch operations and Claude API (added so usage tracking has caps to compare against)
INSERT OR IGNORE INTO config (key, value) VALUES
    ('api_soft_cap.http_fetch',                 '1000'),
    ('api_soft_cap.claude_api',                 '1000');
