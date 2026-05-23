-- 033: Groq's most-frequently-hit cap is tokens-per-day (TPD), but the limits page only
-- tracked TPM. Add a TPD soft cap so it shows up on the dashboard. Also correct the TPM
-- value — Groq's free-tier llama-3.3-70b-versatile is actually 12000 TPM, not 6000.

INSERT OR IGNORE INTO config (key, value) VALUES
    ('api_tpd_cap.groq',   '100000'),
    ('api_tpd_cap.gemini', '0'),
    ('api_tpd_cap.claude', '0');

UPDATE config SET value = '12000' WHERE key = 'api_tpm_cap.groq' AND value = '6000';
