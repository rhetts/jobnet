-- 058: Remember how a job board's employer names resolve to company domains. T-Net result rows
-- only carry the employer name; when it doesn't match a companies.name, the ingestor opens the
-- employer's profile page (a Playwright fetch, ~30s with rate limiting) to find their website.
-- Without this table that lookup re-ran for every job, every run — including for employers whose
-- profile has no outbound website link (~42 per run, most of the T-Net step's 30+ minutes).
--   domain NOT NULL -> resolved; reused indefinitely (the name->domain mapping is stable).
--   domain NULL     -> profile had no usable website link; retried after a cool-off period.
CREATE TABLE IF NOT EXISTS jobboard_company_map (
    source        TEXT NOT NULL,
    company_name  TEXT NOT NULL COLLATE NOCASE,
    domain        TEXT,
    resolved_at   TEXT NOT NULL,
    PRIMARY KEY (source, company_name)
);
