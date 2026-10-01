-- 059: Remove the daily cap on plain HTTP page fetches. Seeded at 1000/day by 006 purely "so usage
-- tracking has caps to compare against", it became a hard stop when RateLimiter started enforcing
-- every soft cap (a day's directory discovery + ATS detection blew through 3000 and then every
-- page fetch in the app failed until the UTC reset). These fetches cost nothing; politeness to
-- sites is handled by api_min_delay_ms.http_fetch and api_rpm_cap.http_fetch. 0 = no cap.
UPDATE config SET value = '0' WHERE key = 'api_soft_cap.http_fetch';
