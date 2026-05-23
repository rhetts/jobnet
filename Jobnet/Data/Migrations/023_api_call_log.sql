-- 023: Per-call log for RPM/TPM analytics — complements api_usage (daily roll-up).
-- One row per attempted call; token counts populated for AI providers, 0 elsewhere.
CREATE TABLE api_call_log (
    id            INTEGER PRIMARY KEY,
    provider      TEXT NOT NULL,
    called_at     TEXT NOT NULL,
    input_tokens  INTEGER NOT NULL DEFAULT 0,
    output_tokens INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX idx_api_call_log_provider_time ON api_call_log(provider, called_at);

-- Per-provider RPM (requests-per-minute) and TPM (tokens-per-minute) caps. RPD caps already
-- live in api_soft_cap.* from migration 004; this adds the two missing dimensions.
-- Values match each provider's documented free-tier limits as of 2026-05.
INSERT INTO config (key, value) VALUES
    ('api_rpm_cap.gemini',           '10'),
    ('api_tpm_cap.gemini',           '250000'),
    ('api_rpm_cap.claude',           '50'),
    ('api_tpm_cap.claude',           '40000'),
    ('api_rpm_cap.brave_search',     '60'),
    ('api_rpm_cap.google_cse',       '60'),
    ('api_rpm_cap.http_fetch',       '120'),
    ('api_rpm_cap.playwright_fetch', '30'),
    ('api_rpm_cap.ats_greenhouse',   '30'),
    ('api_rpm_cap.ats_lever',        '30'),
    ('api_rpm_cap.ats_ashby',        '30');

-- Tighten gemini RPD to match Google's actual free-tier cap (was 900 — too generous).
UPDATE config SET value = '250' WHERE key = 'api_soft_cap.gemini';
INSERT OR REPLACE INTO config (key, value) VALUES ('api_soft_cap.brave_search', '60');
