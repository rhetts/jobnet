-- 029: Groq as a supplemental AI provider — same OpenAI-compatible API shape as Anthropic,
-- generous free tier (30 RPM, ~1K RPD on llama-3.3-70b-versatile).
INSERT OR IGNORE INTO config (key, value) VALUES
    ('groq_api_key',                 ''),
    ('groq_model',                   'llama-3.3-70b-versatile'),
    ('api_soft_cap.groq',            '1000'),
    ('api_rpm_cap.groq',             '30'),
    ('api_tpm_cap.groq',             '6000'),
    ('api_min_delay_ms.groq',        '2200');
