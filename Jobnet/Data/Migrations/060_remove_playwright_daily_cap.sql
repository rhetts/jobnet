-- 060: Remove the daily cap on Playwright page loads. Seeded at 300/day by 011 ("generous for a
-- personal tool") alongside the change that made every soft cap a hard stop; it was never an
-- intentional limit. Playwright is a local headless browser — no provider, no quota, no cost —
-- and the cap only aborted long jobs (bulk ATS detection) partway through. Politeness to sites is
-- handled by api_min_delay_ms.playwright_fetch and api_rpm_cap.playwright_fetch. 0 = no cap.
UPDATE config SET value = '0' WHERE key = 'api_soft_cap.playwright_fetch';
