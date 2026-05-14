-- Default search terms and aggregator sources (see REQUIREMENTS.md §2.7, §6)

INSERT INTO search_terms (term, type, is_active, date_added) VALUES
    ('Vancouver software company',     'company_discovery', 1, datetime('now')),
    ('Vancouver tech startup',         'company_discovery', 1, datetime('now')),
    ('BC fintech company',             'company_discovery', 1, datetime('now')),
    ('Vancouver SaaS',                 'company_discovery', 1, datetime('now')),
    ('Vancouver game studio',          'company_discovery', 1, datetime('now')),
    ('Vancouver cybersecurity',        'company_discovery', 1, datetime('now')),
    ('BC machine learning company',    'company_discovery', 1, datetime('now')),
    ('software engineer Vancouver',    'job_search',        1, datetime('now')),
    ('developer Vancouver BC',         'job_search',        1, datetime('now')),
    ('backend engineer Vancouver',     'job_search',        1, datetime('now')),
    ('frontend developer Vancouver',   'job_search',        1, datetime('now')),
    ('data engineer Vancouver',        'job_search',        1, datetime('now'));

INSERT INTO aggregator_sources (name, base_url, search_url_template, is_enabled, notes) VALUES
    ('LinkedIn Jobs',     'https://www.linkedin.com',     'https://www.linkedin.com/jobs/search/?keywords={term}&location={city}',     0, 'Aggressively blocks scrapers; opt-in only'),
    ('Indeed.ca',         'https://ca.indeed.com',        'https://ca.indeed.com/jobs?q={term}&l={city}',                              0, 'CAPTCHAs after few requests; opt-in only'),
    ('Glassdoor',         'https://www.glassdoor.ca',     'https://www.glassdoor.ca/Job/jobs.htm?sc.keyword={term}&locT=C&locId={city}', 0, 'Login often required'),
    ('Workopolis',        'https://www.workopolis.com',   'https://www.workopolis.com/jobsearch/find-jobs?ak={term}&l={city}',         0, 'Canadian-focused'),
    ('BC Tech',           'https://wearebctech.com',      'https://wearebctech.com/jobs/?search={term}',                               0, 'BC Tech Association job board'),
    ('Wellfound',         'https://wellfound.com',        'https://wellfound.com/jobs?role={term}&location={city}',                    0, 'Startup-focused (formerly AngelList)');

INSERT INTO config (key, value) VALUES
    ('cities',                       '["Vancouver","Burnaby","Richmond","Surrey","North Vancouver","Coquitlam","New Westminster","West Vancouver"]'),
    ('area_priorities',              '["Software Engineering"]'),
    ('level_priorities',             '["Senior","Staff / Principal"]'),
    ('score_weight_area',            '0.5'),
    ('score_weight_level',           '0.5'),
    ('scrape_delay_ms',              '2000'),
    ('search_engine',                'google_cse'),
    ('google_cse_api_key',           ''),
    ('google_cse_engine_id',         ''),
    ('claude_cli_path',              ''),
    ('claude_extraction_prompt',     '');
