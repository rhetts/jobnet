-- Add Brave Search as an alternative to Google CSE.
-- Brave's free tier is 2000 queries/month (~66/day); we set a soft cap at 60/day.

INSERT OR IGNORE INTO config (key, value) VALUES ('brave_search_api_key', '');
INSERT OR IGNORE INTO config (key, value) VALUES ('api_soft_cap.brave_search', '60');

-- Flip the default search engine to Brave (Google CSE's "search the entire web" was
-- removed for new engines in Jan 2026). Existing google_cse users can opt back via Settings.
UPDATE config SET value = 'brave_search' WHERE key = 'search_engine' AND value = 'google_cse';
