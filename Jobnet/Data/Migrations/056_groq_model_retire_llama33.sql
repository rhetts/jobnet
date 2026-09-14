-- 056: Groq retired every Llama 3.x text model from its catalog (confirmed via
-- GET /openai/v1/models — llama-3.3-70b-versatile now 404s "does not exist or you
-- do not have access to it"). Only bump the value for installs still on the
-- original seeded default from migration 029 — never touch a value the user set
-- themselves, even if it happens to look wrong.
UPDATE config
SET value = 'openai/gpt-oss-120b'
WHERE key = 'groq_model' AND value = 'llama-3.3-70b-versatile';
