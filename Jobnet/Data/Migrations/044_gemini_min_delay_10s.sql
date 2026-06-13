-- 044: Bump Gemini's per-call floor to 10 seconds (6 RPM ceiling).
--
-- Real-world data from 2026-06-13: 21 calls in 4:38 minutes peaking at 5 calls/60s
-- triggered TWO 429s from Google, despite Google's published Flash-Lite limit being
-- 15 RPM. The adaptive controller's 1.5× bump (6500 → 9750ms) didn't prevent the
-- second 429 either — Google appears to enforce stricter limits than published for
-- this account/key/region, or applies a per-second smoothing we can't see.
--
-- 10000ms = 6 RPM. Matches ApiQuotaController.MaxDelayMs, which is the cap the
-- adaptive bump would reach anyway after one 429. Starting at the adaptive ceiling
-- means we never have to pay for the first 429 to discover we need this rate.
--
-- The `api_rpm_cap.gemini = 5` sliding-window cap is left alone — together with
-- the 10s floor they're redundant (the floor dominates), but the cap is the
-- defense if the floor is ever lowered via Settings.

UPDATE config SET value = '10000' WHERE key = 'api_min_delay_ms.gemini';
