-- 045: Switch the default Gemini model from flash-lite to flash.
--
-- Observed 2026-06-13: this account's free-tier RPD for gemini-2.5-flash-lite is just 20.
-- Google's published 1000 RPD figure doesn't apply here — account-specific throttling
-- pushed us into HTTP 429 after 21 calls. Full Flash is a *separate* quota bucket per
-- Google's docs, so flipping to flash gives us untouched daily headroom (and slightly
-- better quality answers).
--
-- Unconditional update: even existing installs get switched, because the prior default
-- (flash-lite) is the model that demonstrably 429'd. Users who want flash-lite back can
-- pick it from the Settings dropdown.

UPDATE config SET value = 'gemini-2.5-flash' WHERE key = 'gemini_model';
