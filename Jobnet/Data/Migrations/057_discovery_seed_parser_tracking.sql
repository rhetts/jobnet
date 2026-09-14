-- 057: Track hand-written directory parser status per discovery_seeds row, mirroring the
-- per-company parser_strategy_last_* columns on `companies` (001/029-era). Lets the Parser
-- Report screen show which directories have a custom deterministic parser, whether it's
-- currently working, and the last error if it broke — instead of custom-parser failures
-- silently falling back to AI with nothing surfaced in the UI.
ALTER TABLE discovery_seeds ADD COLUMN custom_parser_name TEXT;
ALTER TABLE discovery_seeds ADD COLUMN custom_parser_last_result TEXT;       -- 'ok' | 'error' | NULL (never run)
ALTER TABLE discovery_seeds ADD COLUMN custom_parser_last_error TEXT;
ALTER TABLE discovery_seeds ADD COLUMN custom_parser_last_result_at TEXT;
