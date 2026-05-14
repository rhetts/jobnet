-- Per-provider daily API call counter + soft caps.
-- Soft caps are configurable; the tracker warns when today's count crosses the cap.

CREATE TABLE api_usage (
    provider    TEXT NOT NULL,
    date        TEXT NOT NULL,  -- 'yyyy-MM-dd' UTC
    count       INTEGER NOT NULL DEFAULT 0,
    last_call   TEXT NOT NULL,
    PRIMARY KEY (provider, date)
);

CREATE INDEX idx_api_usage_date ON api_usage(date);

-- Default soft caps. Stored in config so the user can change them per provider.
-- Google CSE free tier = 100/day; warn at 80 to give a margin.
-- ATS APIs are uncapped publicly but we want to be polite to free endpoints.
INSERT INTO config (key, value) VALUES
    ('api_soft_cap.google_cse',       '80'),
    ('api_soft_cap.bing_search',      '800'),
    ('api_soft_cap.ats_greenhouse',   '500'),
    ('api_soft_cap.ats_lever',        '500'),
    ('api_soft_cap.ats_ashby',        '500'),
    ('api_soft_cap.ats_workable',     '500'),
    ('api_soft_cap.ats_smartrecruiters', '500'),
    ('api_soft_cap.claude_cli',       '500'),
    ('api_soft_cap.claude_haiku',     '1000');
