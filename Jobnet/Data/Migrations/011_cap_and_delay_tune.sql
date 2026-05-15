-- Tighten Gemini pacing (free tier per-minute counter is unforgiving in practice — 12s was still triggering 429s).
-- 20000ms = 3 RPM, deep margin under the 20 RPM server-side cap.
UPDATE config SET value = '20000'
WHERE key = 'api_min_delay_ms.gemini' AND CAST(value AS INTEGER) < 20000;

-- Soft cap on Playwright fetches (was uncapped). 300/day is generous for a personal tool.
INSERT OR IGNORE INTO config (key, value) VALUES ('api_soft_cap.playwright_fetch', '300');

-- Cleanup leftover test config keys from earlier rate-limiter unit tests.
DELETE FROM config WHERE key IN ('_dummy', 'api_min_delay_ms.test_fake_provider', 'api_min_delay_ms.test_no_delay');
