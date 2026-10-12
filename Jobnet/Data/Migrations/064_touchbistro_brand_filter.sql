-- 064: TouchBistro's ATS slug is Harris Computer's Workday tenant (harriscomputer.wd3/1), which lists
-- every Harris business (~240 postings); only ~3 are TouchBistro's. Workday reports no department, so
-- for Workday the filter is passed to the board's own search instead (IKeywordFilteredJobSource).
UPDATE companies SET ats_department_filter = 'TouchBistro'
WHERE domain = 'touchbistro.com' AND ats_slug LIKE 'harriscomputer.%' AND ats_department_filter IS NULL;
