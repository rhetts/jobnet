-- gemini-2.5-flash-lite hit a strict 20 RPM cap during testing while gemini-2.5-flash
-- (full flash) has the same cap but a separate counter / different per-project quota.
-- Flip the default for new installs. Existing installs unchanged (use Settings → AI to switch).

UPDATE config SET value = 'gemini-2.5-flash'
WHERE key = 'gemini_model' AND value = 'gemini-2.5-flash-lite';
