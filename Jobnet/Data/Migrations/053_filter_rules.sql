-- 053: Unified filter rules. One table for every "list" in the app — blocklists, allowlists,
-- greylists, boost lists — replacing hardcoded C# arrays scattered across six files.
--
-- Before this, the same job was done by four divergent host blocklists that disagreed with
-- each other (only DomainExtractor handled subdomains, so ca.linkedin.com passed the
-- harvester's filters but not the discovery one), plus a skip list in UrlClassifier that was
-- dead code — it ran AFTER the positive classification patterns, so it never changed a result.
--
-- Columns:
--   subject     what gets tested. 'host' = a canonical domain, 'url' = a full absolute URL.
--               Later phases add 'job_title', 'company_name', 'location', 'search_token'.
--   action      block | allow | greylist | boost. 'allow' wins over 'block' — needed for
--               location rules in a later phase ("any Canada signal beats a US signal").
--   match_type  regex | substring | domain | exact | word.
--               'domain' = exact host match OR any subdomain of it (linkedin.com blocks
--               ca.linkedin.com). 'word' = whole-word, for the job-title greylist in phase 2.
--               Forcing all 400 legacy entries into regex would silently change behaviour on
--               hundreds of them, so the match semantics travel with the row.
--   scope       crawl | discovery | NULL. NULL means everywhere. This is load-bearing:
--               '/category/' must apply when crawling a careers site but is meaningless for
--               company discovery, and the big-tech entries below must NOT stop you crawling
--               microsoft.com if you deliberately added it as a company to track.
--
-- hit_count / last_hit are written back at the end of a run so you can see which rules earn
-- their keep and answer "why was this URL dropped".

CREATE TABLE filter_rule (
    id          INTEGER PRIMARY KEY,
    subject     TEXT NOT NULL,
    action      TEXT NOT NULL DEFAULT 'block',
    match_type  TEXT NOT NULL DEFAULT 'regex',
    pattern     TEXT NOT NULL,
    scope       TEXT,
    note        TEXT,
    is_enabled  INTEGER NOT NULL DEFAULT 1,
    hit_count   INTEGER NOT NULL DEFAULT 0,
    last_hit    TEXT,
    date_added  TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(subject, action, pattern, scope),
    CHECK (subject    IN ('host','url','job_title','company_name','location','search_token')),
    CHECK (action     IN ('block','allow','greylist','boost')),
    CHECK (match_type IN ('regex','substring','domain','exact','word'))
);

CREATE INDEX idx_filter_rule_lookup ON filter_rule (subject, is_enabled);

-- ── Hosts blocked everywhere (scope NULL) ──────────────────────────────────
-- These are never a company we track and never a page worth rendering. Sourced from
-- DomainExtractor.Skip, which was the only one of the four legacy lists to handle subdomains.

INSERT INTO filter_rule (subject, action, match_type, pattern, scope, note) VALUES
-- Job aggregators — we want companies, not job listings
('host','block','domain','linkedin.com',        NULL,'Job aggregator'),
('host','block','domain','indeed.com',          NULL,'Job aggregator'),
('host','block','domain','indeed.ca',           NULL,'Job aggregator'),
('host','block','domain','glassdoor.com',       NULL,'Job aggregator'),
('host','block','domain','glassdoor.ca',        NULL,'Job aggregator'),
('host','block','domain','monster.com',         NULL,'Job aggregator'),
('host','block','domain','monster.ca',          NULL,'Job aggregator'),
('host','block','domain','workopolis.com',      NULL,'Job aggregator'),
('host','block','domain','ziprecruiter.com',    NULL,'Job aggregator'),
('host','block','domain','wellfound.com',       NULL,'Job aggregator'),
('host','block','domain','angel.co',            NULL,'Job aggregator'),
('host','block','domain','angellist.com',       NULL,'Job aggregator'),
('host','block','domain','simplyhired.com',     NULL,'Job aggregator'),
('host','block','domain','simplyhired.ca',      NULL,'Job aggregator'),
-- Social
('host','block','domain','facebook.com',        NULL,'Social'),
('host','block','domain','twitter.com',         NULL,'Social'),
('host','block','domain','x.com',               NULL,'Social'),
('host','block','domain','instagram.com',       NULL,'Social'),
('host','block','domain','youtube.com',         NULL,'Social'),
('host','block','domain','tiktok.com',          NULL,'Social'),
('host','block','domain','substack.com',        NULL,'Social / newsletter'),
-- Reference / aggregator profiles
('host','block','domain','wikipedia.org',       NULL,'Reference site'),
('host','block','domain','crunchbase.com',      NULL,'Company-profile aggregator'),
('host','block','domain','quora.com',           NULL,'Reference site'),
('host','block','domain','medium.com',          NULL,'Blog platform'),
('host','block','domain','github.com',          NULL,'Code host, not a company site'),
('host','block','domain','gitlab.com',          NULL,'Code host, not a company site'),
('host','block','domain','investopedia.com',    NULL,'Reference site'),
-- News
('host','block','domain','businessinsider.com', NULL,'News'),
('host','block','domain','techcrunch.com',      NULL,'News'),
('host','block','domain','forbes.com',          NULL,'News'),
('host','block','domain','bloomberg.com',       NULL,'News'),
('host','block','domain','betakit.com',         NULL,'News'),
('host','block','domain','biv.com',             NULL,'News'),
('host','block','domain','globalnews.ca',       NULL,'News'),
('host','block','domain','cbc.ca',              NULL,'News'),
-- BC/Canada tech news + blog directories. These surfaced as "companies" and produced
-- misclassified "Founder"/"CTO" jobs extracted from article headlines.
('host','block','domain','vantechjournal.com',  NULL,'BC tech news — produced fake jobs from headlines'),
('host','block','domain','bcbusiness.ca',       NULL,'BC tech news — produced fake jobs from headlines'),
('host','block','domain','techcouver.com',      NULL,'BC tech news — produced fake jobs from headlines'),
('host','block','domain','cleanenergy.ca',      NULL,'BC tech news — produced fake jobs from headlines'),
('host','block','domain','beststartup.ca',      NULL,'BC tech news — produced fake jobs from headlines'),
('host','block','domain','dailyhive.com',       NULL,'BC tech news — produced fake jobs from headlines'),
('host','block','domain','bctechnology.com',    NULL,'BC tech news — produced fake jobs from headlines'),
-- B2B review / firm-directory sites — they list services, not the companies we want
('host','block','domain','clutch.co',           NULL,'B2B review directory'),
('host','block','domain','goodfirms.co',        NULL,'B2B review directory'),
('host','block','domain','themanifest.com',     NULL,'B2B review directory'),
('host','block','domain','designrush.com',      NULL,'B2B review directory'),
('host','block','domain','sortlist.com',        NULL,'B2B review directory'),
('host','block','domain','g2.com',              NULL,'B2B review directory'),
('host','block','domain','capterra.com',        NULL,'B2B review directory'),
('host','block','domain','trustpilot.com',      NULL,'B2B review directory'),
-- Startup directories
('host','block','domain','topstartups.io',      NULL,'Startup directory'),
('host','block','domain','startus-insights.com',NULL,'Startup directory'),
('host','block','domain','startups-list.com',   NULL,'Startup directory'),
('host','block','domain','openvc.app',          NULL,'Startup directory'),
('host','block','domain','getlatka.com',        NULL,'Startup directory'),
('host','block','domain','builtin.com',         NULL,'Startup directory'),
('host','block','domain','builtinvancouver.org',NULL,'Startup directory'),
-- Industry-specific directories
('host','block','domain','gamecompanies.com',   NULL,'Industry directory'),
('host','block','domain','canadiangamedevs.com',NULL,'Industry directory'),
('host','block','domain','thisgamestudio.com',  NULL,'Industry directory'),
('host','block','domain','cloudtango.net',      NULL,'Industry directory'),
('host','block','domain','fintechcadence.com',  NULL,'Industry directory'),
('host','block','domain','ainbc.ai',            NULL,'Industry directory'),
-- Lead-gen / scrapers (not real companies)
('host','block','domain','aeroleads.com',       NULL,'Lead-gen scraper'),
('host','block','domain','zoominfo.com',        NULL,'Lead-gen scraper'),
('host','block','domain','rocketreach.co',      NULL,'Lead-gen scraper'),
-- Canadian gov / education / regulatory
('host','block','domain','britishcolumbia.ca',  NULL,'Government'),
('host','block','domain','canada.ca',           NULL,'Government'),
('host','block','domain','gc.ca',               NULL,'Government'),
('host','block','domain','ic.gc.ca',            NULL,'Government'),
('host','block','domain','innovation.ca',       NULL,'Government'),
('host','block','domain','bcsc.bc.ca',          NULL,'Regulator — multi-label domain, must not false-match *.bc.ca companies'),
('host','block','domain','bcit.ca',             NULL,'Education'),
('host','block','domain','vcc.ca',              NULL,'Education'),
-- Generic / search engines + utilities
('host','block','domain','google.com',          NULL,'Search engine'),
('host','block','domain','maps.google.com',     NULL,'Search engine'),
('host','block','domain','goo.gl',              NULL,'URL shortener'),
('host','block','domain','bing.com',            NULL,'Search engine'),
('host','block','domain','duckduckgo.com',      NULL,'Search engine');

-- ── Hosts blocked in DISCOVERY only ────────────────────────────────────────
-- "Don't auto-add these as companies to track." Deliberately NOT scoped to crawl: if the user
-- adds one of these by hand, refreshing it should still work.

INSERT INTO filter_rule (subject, action, match_type, pattern, scope, note) VALUES
('host','block','domain','microsoft.com', 'discovery','Big tech — not a discovery target'),
('host','block','domain','amazon.com',    'discovery','Big tech — not a discovery target'),
('host','block','domain','apple.com',     'discovery','Big tech — not a discovery target'),
('host','block','domain','meta.com',      'discovery','Big tech — not a discovery target'),
('host','block','domain','oracle.com',    'discovery','Big tech — not a discovery target'),
('host','block','domain','sap.com',       'discovery','Big tech — not a discovery target'),
('host','block','domain','salesforce.com','discovery','Big tech — not a discovery target'),
('host','block','domain','adobe.com',     'discovery','Big tech — not a discovery target'),
('host','block','domain','ibm.com',       'discovery','Big tech — not a discovery target'),
('host','block','domain','intel.com',     'discovery','Big tech — not a discovery target'),
('host','block','domain','cisco.com',     'discovery','Big tech — not a discovery target'),
('host','block','domain','dell.com',      'discovery','Big tech — not a discovery target'),
('host','block','domain','hp.com',        'discovery','Big tech — not a discovery target'),
('host','block','domain','stripe.com',    'discovery','Big tech — not a discovery target'),
('host','block','domain','square.com',    'discovery','Big tech — not a discovery target'),
('host','block','domain','block.xyz',     'discovery','Big tech — not a discovery target'),
-- Discovery-scoped rather than global, because each of these is ALSO an active row in
-- companies: Y Combinator, Techstars (x2), BC's ScaleUp Opportunity and reddit are tracked
-- and must stay crawlable. Their harm is "don't auto-harvest them as new companies", which
-- is precisely what 'discovery' scope expresses. Vanta sits on account.ycombinator.com, so
-- a global rule here would have silently killed a real company's refresh.
('host','block','domain','ycombinator.com','discovery','Accelerator directory'),
('host','block','domain','techstars.com',  'discovery','Accelerator directory'),
('host','block','domain','wearebctech.com','discovery','Startup directory'),
('host','block','domain','reddit.com',     'discovery','Social');

-- ── News sites that were being tracked as companies ────────────────────────
-- Each of these was in `companies` and producing nothing but fabricated jobs scraped out of
-- article headlines and page navigation:
--   thelogic.co          → "FAQs", "Advertise", "Contact Us", "Privacy Statement"
--   vancouver.nyit.edu   → "Career Services", "Policies", "Communications"
--   fintech.ca           → "Apply" x4, plus a press-release headline
--   fundraiseinsider.com → 36 funding-round headlines ("Redo Raises $81M Series B…")
-- Blocking the host stops discovery re-adding them; the UPDATE below stops the refresher
-- visiting the rows that already exist. Both are needed — one without the other leaves a hole.

INSERT INTO filter_rule (subject, action, match_type, pattern, scope, note) VALUES
('host','block','domain','thelogic.co',         NULL,'News site — only ever produced fake jobs from headlines'),
('host','block','domain','fintech.ca',          NULL,'News site — only ever produced fake jobs from headlines'),
('host','block','domain','fundraiseinsider.com',NULL,'Funding-news blog — only ever produced fake jobs from headlines'),
('host','block','domain','nyit.edu',            NULL,'University news — only ever produced fake jobs from nav links');

UPDATE companies
   SET is_blacklisted = 1
 WHERE domain IN ('thelogic.co', 'fintech.ca', 'fundraiseinsider.com', 'vancouver.nyit.edu');

-- ── URL path rules, CRAWL only ─────────────────────────────────────────────
-- These are what stop the AI-extract path burning Playwright renders and AI quota on pages
-- that structurally cannot contain job postings.
--
-- The Blue Ant Media case: its WordPress newsroom put 35 /category/all-news/page/N/ archives
-- into company_urls, classified as 'department' because UrlClassifier's DepartmentPath regex
-- matches /category/<anything>. JobRefresher then crawled 10 of them per run, each triggering
-- a 2-hop follow-up, to rediscover the same 8 Dayforce jobs. One run took 40 minutes and
-- examined exactly one company.
--
-- Deliberately NOT included: a bare '/page/\d+' rule. A real board can legitimately paginate
-- (/jobs/page/2). The '/category/' rule alone kills all 35 Blue Ant URLs, so the riskier
-- pattern isn't needed. Also omitted: '/about' and '/security' — '/about/careers' is a common
-- WordPress convention and 'security' is a real engineering department.

INSERT INTO filter_rule (subject, action, match_type, pattern, scope, note) VALUES
('url','block','regex','/(category|tag|author)/',                    'crawl','WordPress archive — news, not jobs. Blue Ant had 35 of these.'),
('url','block','regex','/20\d\d/\d\d/',                              'crawl','Dated permalink — a blog post, not a posting'),
('url','block','regex','/(login|signin|sign-in|signup|register)(/|$|\?)','crawl','Auth page'),
-- 'legal' is deliberately absent from this alternation: Y Combinator's board uses
-- /jobs/l/legal as its legal-roles category, and Legal is a real hiring department.
('url','block','regex','/(privacy|cookie-policy|cookies|terms|terms-of-service)(/|$|\?)','crawl','Policy page'),
('url','block','regex','/(feed|rss)(/|$)',                           'crawl','Syndication feed'),
('url','block','regex','/wp-(admin|login|content|json)/',            'crawl','WordPress internals');
