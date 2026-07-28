-- 055: Retire the fabricated jobs left behind by the news sites blacklisted in 053.
--
-- Blacklisting a company stops future refreshes; it does not touch rows already in `jobs`. That
-- left 41 active postings that were never postings at all — the AI extracted them from article
-- headlines and page navigation on sites that have no careers page:
--
--   fundraiseinsider.com  36  "Redo Raises $81M Series B for Commerce Platform", …
--   fintech.ca             5  "Apply" x4, plus a press-release headline
--
-- (thelogic.co and vancouver.nyit.edu were also blacklisted in 053 but have no active rows.)
--
-- Marked removed rather than deleted, matching how JobRefresher retires a posting that has
-- disappeared from a board — the history stays queryable and the UI's "show removed" toggle can
-- still surface them. Scoped by domain rather than company id so it reads as what it is.
--
-- Deliberately NOT touching the companies blacklisted in 054 for being slow: fasken.com and
-- akkodis.com have genuine live postings, and their problem was a hang, not bad data.

UPDATE jobs
   SET is_active    = 0,
       date_removed = COALESCE(date_removed, datetime('now'))
 WHERE is_active = 1
   AND company_id IN (
        SELECT id FROM companies
         WHERE domain IN ('fundraiseinsider.com', 'fintech.ca', 'thelogic.co', 'vancouver.nyit.edu')
   );
