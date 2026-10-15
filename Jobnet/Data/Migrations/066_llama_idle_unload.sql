-- 066: Unload the local Llama model after it has gone this many minutes without an inference
-- (~5 GB for the 8B Q4 model plus context). The next AI call reloads it in a few seconds.
-- 0 = keep it loaded for the life of the app (the old behaviour).
INSERT OR IGNORE INTO config (key, value) VALUES ('llama_idle_unload_minutes', '10');
