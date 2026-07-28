-- 054: Blacklist companies that have ever taken more than an hour on a single refresh attempt.
--
-- Measured from refresh_attempt.duration_ms, which has recorded per-company, per-stage timing
-- since June. Six companies qualify:
--
--   hyperwallet.com         worst 18.80 h   total 19.96 h    8 jobs ever
--   fasken.com              worst 18.32 h   total 19.57 h   72 jobs ever   (5 live at time of writing)
--   businessinrichmond.ca   worst  7.92 h   total  8.02 h    0 jobs ever
--   venturelabs.ca          worst  6.27 h   total  9.44 h   56 jobs ever
--   forterra.com            worst  2.98 h   total  3.33 h    6 jobs ever
--   akkodis.com             worst  1.46 h   total  2.70 h   53 jobs ever   (1 live)
--
-- CAVEAT worth recording here rather than losing: multi-hour times are almost certainly a HANG,
-- not a genuinely slow site. PlaywrightFetcher caps network-idle at 30s, so an 18-hour
-- 'cached_url' attempt points at an unbounded wait further down — most likely LLamaClient, which
-- is where everything lands once Gemini's 20-request free daily tier 429s. Three of these six
-- (fasken, venturelabs, akkodis) were producing real jobs; blacklisting treats the symptom.
-- Fixing the missing timeout would likely make them viable again.
--
-- Blacklisting stops future refreshes only. Existing jobs are left alone and simply go stale.
-- To reverse one:  UPDATE companies SET is_blacklisted = 0 WHERE domain = '<domain>';

UPDATE companies
   SET is_blacklisted = 1
 WHERE domain IN (
        'hyperwallet.com',
        'fasken.com',
        'businessinrichmond.ca',
        'venturelabs.ca',
        'forterra.com',
        'akkodis.com'
 );
