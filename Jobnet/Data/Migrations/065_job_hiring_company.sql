-- 065: The real employer behind an agency posting. S.i. Systems' feed names the client (WorkSafeBC,
-- BC Hydro, Vancity...) but the adapter had nowhere to put it except description_snippet as
-- "Client: X" -- shown only in the expanded card, and hidden entirely once an AI summary existed.
ALTER TABLE jobs ADD COLUMN hiring_company TEXT;

-- Move existing S.i. Systems clients out of the description.
UPDATE jobs
SET hiring_company = trim(substr(description_snippet, 9)),
    description_snippet = NULL
WHERE description_snippet LIKE 'Client: %'
  AND company_id IN (SELECT id FROM companies WHERE domain = 'sisystems.com');

-- S.i. Systems is a staffing agency; flag it so its cards get the Agency chip.
UPDATE companies SET is_agency = 1 WHERE domain = 'sisystems.com';
