-- 035: Local LLamaSharp client config. Model file (.gguf) lives outside the repo — user
-- points llama_model_path at a downloaded model (e.g. Meta-Llama-3.1-8B-Instruct.Q4_K_M.gguf).
-- Everything is optional; the client only becomes IsConfigured when the file actually exists.

INSERT OR IGNORE INTO config (key, value) VALUES
    ('llama_model_path',     ''),
    ('llama_context_size',   '4096'),
    ('llama_max_tokens',     '1024'),
    ('llama_threads',        '0'),       -- 0 = auto (ProcessorCount)
    ('llama_gpu_layers',     '0');       -- 0 = pure CPU; raise for GPU offload

-- Per-task provider routing. Each key overrides the global ai_provider chain for that task.
-- Default empty = use global ai_provider. Values are comma-separated provider names from
-- RoutingAiClient.KnownProviders (gemini, groq, claude, llama).
INSERT OR IGNORE INTO config (key, value) VALUES
    ('ai_provider.extraction',    ''),
    ('ai_provider.directory',     ''),
    ('ai_provider.resume_match',  ''),
    ('ai_provider.summary',       ''),
    ('ai_provider.profile',       ''),
    ('ai_provider.cover_letter',  ''),
    ('ai_provider.classifier',    ''),
    ('ai_provider.competitors',   '');
