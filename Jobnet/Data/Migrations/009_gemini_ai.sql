-- Make the AI provider swappable. Default to Gemini (more generous free tier than Claude).
-- The old claude_* config keys are kept so users can switch back via ai_provider.

INSERT OR IGNORE INTO config (key, value) VALUES
    ('ai_provider',                       'gemini'),
    ('gemini_api_key',                    ''),
    ('gemini_model',                      'gemini-2.5-flash-lite'),
    ('gemini_max_tokens_classify',        '256'),
    ('gemini_max_tokens_profile',         '1024'),
    ('api_min_delay_ms.gemini',           '4500'),   -- free tier ~15 RPM, this gives margin
    ('api_soft_cap.gemini',               '900');    -- under typical 1000 RPD free-tier ceiling
