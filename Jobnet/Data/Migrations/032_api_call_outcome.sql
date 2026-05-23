-- 032: Capture HTTP status + error body on api_call_log so failures (especially Groq 429s)
-- are diagnosable after the fact. Previously we only stored called_at + tokens, so failed
-- calls showed up only as 0/0-token rows with no explanation. NULL on existing rows means
-- "outcome unknown" (logged before this migration).

ALTER TABLE api_call_log ADD COLUMN status_code   INTEGER;
ALTER TABLE api_call_log ADD COLUMN error_message TEXT;
