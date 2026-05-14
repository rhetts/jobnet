-- Claude API key + model config. Optional — when unset, classifier/profiler fall back gracefully.

INSERT OR IGNORE INTO config (key, value) VALUES
    ('claude_api_key',  ''),
    ('claude_model',    'claude-haiku-4-5'),
    ('claude_max_tokens_classify', '256'),
    ('claude_max_tokens_profile',  '1024');
