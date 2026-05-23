-- 015: Some ATS boards are shared across multiple brands (e.g. Match Group's
-- Lever board lists Hinge, Tinder, Plenty of Fish, etc. under one slug).
-- When set, JobRefresher will only keep raw postings whose ATS-reported
-- department equals this string (case-insensitive).
ALTER TABLE companies ADD COLUMN ats_department_filter TEXT;

-- Plenty of Fish shares Match Group's "matchgroup" Lever slug.
UPDATE companies SET ats_department_filter = 'Plenty of Fish' WHERE domain = 'pof.com';
