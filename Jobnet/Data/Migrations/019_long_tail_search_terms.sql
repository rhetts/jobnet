-- 019: Add long-tail search terms aimed at lesser-known Vancouver tech companies.
-- The default seed used generic queries ("Vancouver software company") that returned
-- the same well-known names every time. These narrower queries surface YC-backed,
-- Series-A/B-funded, and aggregator-listed startups instead.
INSERT OR IGNORE INTO search_terms (term, type, is_active, date_added) VALUES
    ('Vancouver Series A startup',          'company_discovery', 1, datetime('now')),
    ('Vancouver Series B startup',          'company_discovery', 1, datetime('now')),
    ('Vancouver Y Combinator company',      'company_discovery', 1, datetime('now')),
    ('Vancouver Techstars company',         'company_discovery', 1, datetime('now')),
    ('BC startup hiring engineers',         'company_discovery', 1, datetime('now')),
    ('Vancouver dev tools company',         'company_discovery', 1, datetime('now')),
    ('Vancouver AI startup',                'company_discovery', 1, datetime('now')),
    ('Vancouver fintech startup',           'company_discovery', 1, datetime('now')),
    ('Vancouver gaming studio independent', 'company_discovery', 1, datetime('now')),
    ('Vancouver health tech startup',       'company_discovery', 1, datetime('now')),
    ('Vancouver climate tech startup',      'company_discovery', 1, datetime('now')),
    ('Vancouver crypto blockchain company', 'company_discovery', 1, datetime('now')),
    ('Burnaby tech company',                'company_discovery', 1, datetime('now')),
    ('Richmond BC software company',        'company_discovery', 1, datetime('now')),
    ('Vancouver scale-up tech',             'company_discovery', 1, datetime('now')),
    ('Yaletown Partners portfolio',         'company_discovery', 1, datetime('now')),
    ('Version One Ventures portfolio',      'company_discovery', 1, datetime('now')),
    ('BDC Capital Vancouver portfolio',     'company_discovery', 1, datetime('now'));
