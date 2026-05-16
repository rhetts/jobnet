# Jobnet — Requirements Document

**Version:** 0.10 (Filters, salary, seed-companies bulk import)
**Last Updated:** 2026-05-15
**Purpose:** Living requirements document. Update as decisions are made and scope evolves.

---

## 1. Overview

Jobnet is a Windows desktop application (WPF / C#) for discovering and tracking technology job postings at Vancouver-area companies. The app automates the tedious work of monitoring dozens of company job boards and aggregator sites, maintaining a local database of companies and postings, and surfacing interesting opportunities over time.

### Core value proposition
- One place to see all Vancouver tech companies and their open roles
- **Find jobs others can't** — direct company career page scraping reaches small BC companies, stealth startups, and firms that don't post to aggregators
- Automatic re-scanning keeps data fresh without manual browsing
- Age tracking reveals how long a role has been open (signal of urgency or stagnation)
- Interest tagging lets you focus on what matters and ignore what doesn't

### Architecture clarification: what Claude does vs. what the app does
- **The app** does all HTTP fetching (company discovery via Brave Search API or Google CSE; job page fetches via direct HTTP / headless browser)
- **Claude CLI** only receives already-fetched HTML and parses it into structured JSON — it does not browse the internet
- Free tier limits apply only to *company discovery* searches (a weekly or on-demand operation). All ongoing job scraping is direct HTTP to known careers URLs — no search-engine involvement.

---

## 2. Functional Requirements

### 2.1 Company Discovery

- The app maintains a list of **search terms** used to find companies (e.g., "Vancouver software company", "BC tech startup", "Vancouver fintech").
- Search terms are stored in the database and editable through the UI.
- A discovery run executes each search term against configured sources (search engines, aggregator sites) and extracts candidate company names and URLs.
- Discovered companies are deduplicated by domain name before being added to the database.
- Companies may also be added manually by the user.
- Each company record stores: name, website URL, careers page URL (if known), industry/category tags, location (city/region), date first discovered, date last scanned, notes.

**Decision:** The default search engine is **Brave Search API** (free tier 2000 queries/month). Google Custom Search is supported as an alternative but is **no longer recommended for new engines** — Google deprecated "Search the entire web" mode for new CSEs as of 2026-01-20, restricting them to at most 50 specific sites. Existing google_cse engines created before that date continue to work but lose web-wide search on 2027-01-01.

The provider is selected via the `search_engine` config key (`brave_search` or `google_cse`). Both clients implement `ISearchClient`; a `RoutingSearchClient` selects the right one at call time based on config. API credentials are entered in Settings.

| Provider | Free tier | Daily soft cap (default) | Notes |
|---|---|---|---|
| Brave Search | 2000/month | 60/day | Primary recommendation. Get key at https://api.search.brave.com/app/keys |
| Google CSE | 100/day | 80/day | Limited to 50 specific sites for new engines |

**Decision:** Geographic scope defaults to **Metro Vancouver** (City of Vancouver, Burnaby, Richmond, Surrey, North Vancouver, Coquitlam, New Westminster, etc.). The city/region list is configurable — a checklist in Settings lets the user add or remove cities. Search terms are automatically scoped to the selected cities.

### 2.2 Job Board Scraping

- For each tracked company, the app fetches its careers page and extracts job postings using the **Parser Registry** (see §2.3).
- The app also scrapes **job aggregator sites** filtered to the configured cities.
- **Decision:** Aggregator sources are **disabled by default**. The Settings view shows a pre-populated list of known aggregators (see §6), each with an enable/disable toggle. The user opts in to each one explicitly.
- Scraping respects reasonable rate limits (configurable delay between requests, default 2 seconds).
- Failed fetches are logged with error reason; the app retries on the next refresh cycle.
- **Decision:** HTML is pre-stripped before any Claude invocation — `<script>`, `<style>`, `<svg>`, `<nav>`, `<footer>`, inline base64 images, and excess whitespace are removed via HtmlAgilityPack. Typical 80-95% token reduction. This is mandatory, not optional.
- **Decision:** A `page_fetches` cache table (see §5) stores the SHA-256 of each fetched page. If the content hash matches the previous successful fetch, Claude is skipped and the prior extraction result is reused. This avoids redundant API calls when nothing changed.

### 2.3 Parser Registry

Each company is assigned a **parser strategy** at discovery time (and re-detected on each scan). The registry is evaluated in priority order:

| Priority | Strategy | Trigger | Output |
|---|---|---|---|
| 1 | **ATS API adapter** | Domain or ATS fingerprint detected | JSON from public API — no HTML parsing needed |
| 2 | **Per-company custom parser** | Domain matches a registered custom parser | C# code, hand-tuned for that company's site |
| 3 | **Headless browser + Claude** | Fallback for all others | Pre-stripped HTML → Claude CLI → JSON |

**ATS API adapters (v1 targets):**

| ATS | API endpoint pattern | Detection |
|---|---|---|
| Greenhouse | `https://boards-api.greenhouse.io/v1/boards/{slug}/jobs` | `boards.greenhouse.io` in careers URL |
| Lever | `https://api.lever.co/v0/postings/{slug}` | `jobs.lever.co` in careers URL |
| Ashby | `https://api.ashbyhq.com/posting-api/job-board/{slug}` | `jobs.ashbyhq.com` in careers URL |
| Workable | `https://{slug}.workable.com/api/v3/jobs` | `apply.workable.com` in careers URL |
| SmartRecruiters | `https://api.smartrecruiters.com/v1/companies/{slug}/postings` | `careers.smartrecruiters.com` in careers URL |

- ATS slug is extracted from the careers page URL or link-followed from the company's `/careers` page.
- ATS adapters return perfectly structured data; no Claude call required.
- **Per-company custom parsers** are C# classes implementing `IJobParser`, stored in a `Parsers/` folder, and registered by domain at startup. Useful for large companies with non-standard or headless-heavy career pages (e.g., Shopify, Amazon Canada, EA, TELUS). These are added incrementally as needed — not a blocking dependency for v1 launch.
- The parser strategy used for each fetch is recorded in `page_fetches` for debugging.
- **Decision:** Decouple **company discovery** (find new companies) from **job scraping** (refresh postings for known companies). These are separate operations with separate UI controls and different cadences: discovery runs on demand or weekly; job refresh runs daily or on-demand per company.

### 2.3 Job Posting Model

Each job posting record contains:

| Field | Description |
|---|---|
| `job_id` | Internal auto-increment PK |
| `company_id` | FK to companies table |
| `hash` | Unique content hash (see §2.4) — used to detect duplicates and track the same posting across rescans |
| `title` | Job title as extracted |
| `url` | Direct link to apply / job detail page |
| `location` | Location string as listed |
| `remote_type` | Enum: `on-site`, `hybrid`, `remote`, `unknown` |
| `employment_type` | Enum: `full-time`, `part-time`, `contract`, `unknown` |
| `description_snippet` | First ~500 chars of job description |
| `salary_range` | Raw salary string if present |
| `date_first_seen` | Timestamp when first scraped |
| `date_last_seen` | Timestamp of most recent scan that found this posting |
| `date_removed` | Timestamp when posting disappeared from source (null if still active) |
| `is_active` | Bool — false when posting no longer appears on source |
| `source` | Where it was found: company website domain or aggregator name |
| `interest_level` | User-set: `null` / `interesting` / `not_interesting` |
| `notes` | Free-text user notes on the posting |
| `area_score` | 0–100 score: how well the job's area/discipline matches configured preferences |
| `level_score` | 0–100 score: how well the job's seniority matches configured management level preferences |
| `composite_score` | Weighted combination of area_score and level_score, used for default sort |

### 2.4 Job Posting Hash (Tiered Strategy)

The hash uniquely identifies a posting across rescans. A **tiered strategy** is used based on which parser produced the result — higher tiers are more stable. The tier used is stored in `jobs.hash_tier`.

| Tier | When | Hash Input | Stability |
|---|---|---|---|
| 1 | ATS API response | `ats_name:native_job_id` (e.g. `greenhouse:98765`) | Perfect — ATS IDs are permanent |
| 2 | URL contains a stable slug or numeric ID in path | SHA-256(normalized_url + `\|\|` + normalized_title) | Very good — path IDs survive query-string churn |
| 3 | Claude-extracted, no reliable stable URL | SHA-256(domain + `\|\|` + normalized_title + `\|\|` + normalized_location) | Fragile — last resort |

**URL normalization for Tier 2:**
- Remove all query parameters and fragments
- Lowercase the full URL, strip trailing slash
- Most ATS URL paths contain a stable numeric or slug segment (e.g. `/jobs/98765-senior-engineer`) even when the full URL has session tokens in query params

**Text normalization (all tiers):**
- Lowercase, collapse whitespace, trim, normalize dashes

**On collision:** Two different roles at the same company with identical title+location+URL-path (Tier 3 only) will collide — rare and acceptable. Tiers 1 and 2 have no collision risk from this. Re-posted roles (same hash, `is_active` was false) reactivate the existing row rather than creating a new one.

**Hashing algorithm:** SHA-256, stored as lowercase hex string.

`hash_tier` values: `1` = ATS native ID, `2` = URL+title, `3` = domain+title+location. Tier 3 rows are flagged in the debug/log view as "low-confidence identity."

### 2.5 Refresh / Rescan

Two distinct operations — triggered separately, with separate UI controls:

**Discover Companies** (on-demand or weekly):
1. Executes configured search terms against the configured search engine (Brave by default, Google CSE optional)
2. Extracts candidate company names and career page URLs from results; runs them through `DomainExtractor`'s skip-list to filter aggregators, social, news, B2B review sites, startup directories, lead-gen scrapers, and gov/edu domains
3. Deduplicates by canonical domain (host minus `www.`/`careers.`/`jobs.`/`hire.`); adds new companies to DB
4. Detects ATS provider for each new company; assigns parser strategy (Phase 5)
5. Records the run in `scan_log`; counts API calls in `api_usage`
6. Does NOT scan jobs (that is a separate step)

**Refresh Jobs** (on-demand or daily, per-company or global):
1. For each active company:
   a. Check `page_fetches` cache — skip Claude if content hash unchanged
   b. Fetch careers page via appropriate parser (ATS API / custom / headless+Claude)
   c. Diff extracted postings against DB using hashes
   d. Add new postings; update `date_last_seen` on existing ones
   e. **Only** mark postings as `is_active = false` if the source fetch was **successful** and the posting was absent
   f. Record fetch result in `page_fetches` and `scan_log`
2. A failed fetch (network error, 4xx/5xx, parse failure) logs the error but does **not** change any posting's `is_active` state
3. Runs in a background thread; progress shown in status bar
4. **Dry-run mode:** Fetches and extracts but writes nothing to DB — for debugging prompt changes

Last refresh timestamp is displayed globally and per-company. "Show Refresh Log" button opens a view of `scan_log` with per-company success/failure details.

### 2.6 Claude CLI Integration

When raw HTML is fetched for a page (company careers page or aggregator result), it is piped through **Claude CLI** with a structured prompt that instructs it to extract job postings into a fixed JSON schema:

```json
[
  {
    "title": "...",
    "url": "...",
    "location": "...",
    "remote_type": "on-site|hybrid|remote|unknown",
    "employment_type": "full-time|part-time|contract|unknown",
    "description_snippet": "...",
    "salary_range": "..."
  }
]
```

- The extraction prompt is stored in the app's config so it can be tuned without recompiling.
- **Decision:** Claude CLI is invoked as a **shell subprocess** from C# (`Process.Start`). The path to the `claude` executable is auto-detected from PATH and overridable in Settings. HTML content is passed via a temp file (not stdin pipe) to avoid encoding/buffering issues with large HTML payloads. JSON is parsed from stdout.
- Failures (invalid JSON, empty response) are logged and the raw HTML is preserved for debugging.
- The extraction prompt instructs Claude to also classify `area_category` and `level_category` for scoring (see §2.10).

### 2.7 Search Terms Management

- A dedicated settings view lists all active search terms.
- Each term has a name/label and a type: `company_discovery` (finds companies) or `job_search` (finds job postings directly on aggregators).
- Default seed terms are populated on first launch (editable):
  - Company discovery: "Vancouver software company", "Vancouver tech startup", "BC fintech company", "Vancouver SaaS", "Vancouver game studio", "Vancouver cybersecurity", "BC machine learning company"
  - Job search: "software engineer Vancouver", "developer Vancouver BC", "backend engineer Vancouver", "frontend developer Vancouver", "data engineer Vancouver"
- Terms can be added, edited, or soft-deleted (disabled but retained in history).
- City tokens (from the configured city list) are automatically appended to/substituted into search terms at runtime — the raw term is stored without the city suffix so it works across city config changes.

### 2.8 Interest Levels

- The user can mark any job posting as **Interesting** (thumbs up) or **Not Interesting** (thumbs down).
- Unmarked postings are **Neutral** by default.
- Companies can also be marked as interesting / not interesting at the company level, which acts as a default for all their postings.
- Interest level is persisted in the database and survives rescans.
- **Decision:** Interest marking is **manual only** in v1. See §2.11 for the planned auto-ranking roadmap.

### 2.9 Expired / Removed Postings

- **Decision:** Removed postings (where `is_active = false`) are **hidden by default** in all job lists.
- A **"Show Removed"** checkbox in the job list toolbar toggles their visibility. When shown, removed postings are visually dimmed and display a "Removed [date]" badge.
- Removed postings are retained in the database indefinitely so historical data and interest markings are preserved.

### 2.10 Job Scoring (Area & Level)

Jobs are automatically scored on two axes when extracted, based on user configuration:

**Area score** — how well the job's discipline matches configured preferred areas:
- Configured as a prioritized list of area categories (default: `Software Engineering` at top)
- Area categories: Software Engineering, Data / ML, DevOps / Platform, QA / Test, Security, Product Management, Design, Management, Other
- Claude is asked to classify each job into one area category during extraction
- Jobs matching the top area score 100; secondary areas score lower; unmatched score 0

**Level score** — how well the seniority matches configured preferences:
- Configured as a prioritized list of management/seniority levels (default: `Senior IC`, `Staff`)
- Level categories: Junior, Mid, Senior, Staff / Principal, Lead, Manager, Director, VP+, Unknown
- Claude classifies each job's level during extraction
- Matching levels score 100; partial matches score lower

**Composite score** = weighted average of area_score and level_score (weights configurable, default 50/50). Jobs are sorted by composite score descending within each company view and the global job view.

### 2.11 Auto-Ranking Roadmap (Future)

Manual interest marking creates labeled training data. Future versions could exploit this:
- **Keyword extraction:** Identify terms that appear more often in "Interesting" jobs vs. "Not Interesting" — surface these as suggested filter terms.
- **Naive Bayes or TF-IDF classifier:** Train on the interest-labeled job descriptions to auto-score new postings with a "predicted interest" value (separate from manual interest_level).
- **Company-level learning:** If the user consistently marks a company's jobs as interesting, raise that company's baseline score automatically.
- **UI surface:** A "For You" tab showing highest predicted-interest unreviewed jobs.
- This is explicitly out of scope for v1 but the schema is designed to support it (interest labels are stored, description snippets are retained).

### 2.9 Filtering

Job list filters:
- Interest level: All / Interesting only / Neutral / Not Interesting hidden (default)
- Active only (default) / include removed (checkbox)
- Search by keyword (matches title and description snippet)
- Employment type
- Remote type
- Area category (multi-select)
- Level category (multi-select)
- Date first seen (range)
- Sort by: composite score (default) / date first seen / company name / title

Company list filters:
- Interest level (same as above)
- Search by name
- Has active jobs / no active jobs
- Date last scanned

---

## 3. Non-Functional Requirements

- **Platform:** Windows 10/11 desktop, WPF, .NET 8+, C#
- **Database:** SQLite via Dapper (lightweight, explicit SQL — preferred given the schema is small and hand-written SQL is more transparent for a single-dev tool)
- **No server:** Entirely local app, no backend service required
- **Performance:** UI remains responsive during background refresh (async/await throughout)
- **Startup time:** App should be usable within 3 seconds on a modern machine (DB is local)
- **Data portability:** SQLite file is in a well-known location; user can back it up manually

---

## 4. UI / UX

### 4.1 Main Window Layout

```
┌──────────────────────────────────────────────────────────────────┐
│ [Jobnet]    [Discover Companies]  [Refresh Jobs]  [Settings]     │
├──────────────────────┬───────────────────────────────────────────┤
│ [Search companies □] │ All Jobs  ▾  [□ Show Removed] [Filters ▾] │
│ ─────────────────── │ ────────────────────────────────────────── │
│ ★ All Jobs     247  │ ★ Senior Backend Engineer — Acme Corp      │
│ ─────────────────── │   Remote · Full-time · Score 94 · 3d old   │
│ ★ Acme Corp     12  │                                            │
│   Hootsuite      8  │   Staff Engineer — Hootsuite               │
│ ✗ Slack          3  │   Hybrid · Full-time · Score 87 · 14d old  │
│   Coinbase       5  │                                            │
│   ...               │ ✗ Marketing Manager — Coinbase             │
│                     │   On-site · Full-time · Score 12 · 60d old │
└──────────────────────┴───────────────────────────────────────────┘
```

- **"All Jobs"** is a pinned entry at the top of the company list showing all jobs across all companies, sorted by composite score descending by default. This is the primary view for daily use.
- Company name appears as a column/subtitle in the jobs pane when viewing All Jobs.
- The two toolbar buttons are separate: **Discover Companies** runs the search-engine discovery pipeline; **Refresh Jobs** rescans careers pages for all tracked companies. Each has its own progress indicator.

### 4.2 Interactions

- **Click company** → load that company's job postings in the right pane
- **Click job posting** → open the posting's URL in the default browser
- **Right-click job** → context menu: Mark Interesting / Mark Not Interesting / Open in Browser / Copy URL / View Notes
- **Right-click company** → context menu: Mark Interesting / Mark Not Interesting / Rescan Now / Edit / View Website
- **Double-click job** → open detail flyout/panel with full description snippet, all metadata, notes field
- **Refresh All button** → trigger global rescan with a progress bar in the status bar
- **Search box** (top right) → live-filters visible companies by name

### 4.3 Status Bar

- Last global refresh timestamp
- Running refresh progress ("Scanning Acme Corp... 12/47 companies")
- Total companies / total active jobs counts

### 4.4 Settings / Config View

Organized into tabs:

**Search**
- Search engine provider dropdown (`brave_search` default, `google_cse` optional)
- Brave Search API key
- Google CSE API key + engine ID (only used if `google_cse` selected)
- Cities to include (one per line)

**Sources**
- Aggregator sources toggle list (all disabled by default; pre-populated with known sites)

**Categories**
- Levels list (add, delete, reorder via ▲/▼) — drives level scoring priority
- Areas list (same controls) — drives area scoring priority
- Both are closed taxonomies: the classifier only maps to entries that exist here

**Scoring**
- Area weight + level weight (numeric inputs that combine into composite score)

**Scraping**
- Scrape delay (ms between requests, default 2000)
- Claude CLI path (auto-detected if on PATH, overridable)
- Claude extraction prompt (editable text box, resettable to default)

**Data**
- Database file path (with "Open folder" button)
- Export to CSV button
- Danger zone: Clear all job data / Clear all companies

---

## 5. Data Model (SQLite Schema — Draft)

All dates stored as UTC ISO-8601 TEXT (`2026-05-13T19:00:00Z`). Displayed in local time in the UI.

Open connection with: `PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;`

```sql
CREATE TABLE companies (
    id              INTEGER PRIMARY KEY,
    name            TEXT NOT NULL,
    domain          TEXT NOT NULL UNIQUE,   -- root domain, no subdomain
    website_url     TEXT,
    careers_url     TEXT,
    ats_type        TEXT,   -- 'greenhouse'|'lever'|'ashby'|'workable'|'smartrecruiters'|'custom'|'claude'|NULL
    ats_slug        TEXT,   -- slug used in ATS API calls
    parser_strategy TEXT,   -- '1_ats_api'|'2_custom'|'3_claude'
    industry_tags   TEXT,   -- JSON array of strings
    city            TEXT,
    interest_level  TEXT DEFAULT NULL
                    CHECK (interest_level IN ('interesting','not_interesting') OR interest_level IS NULL),
    notes           TEXT,
    date_discovered TEXT NOT NULL,
    date_last_scan  TEXT,
    is_active       INTEGER DEFAULT 1
);

CREATE TABLE jobs (
    id                  INTEGER PRIMARY KEY,
    company_id          INTEGER NOT NULL REFERENCES companies(id),
    hash                TEXT NOT NULL UNIQUE,
    hash_tier           INTEGER NOT NULL,   -- 1=ats_id, 2=url+title, 3=domain+title+location
    title               TEXT NOT NULL,
    url                 TEXT,
    location            TEXT,
    remote_type         TEXT CHECK (remote_type IN ('on-site','hybrid','remote','unknown')),
    employment_type     TEXT CHECK (employment_type IN ('full-time','part-time','contract','unknown')),
    area_category       TEXT,   -- raw Claude classification (e.g. 'Software Engineering')
    level_category      TEXT,   -- raw Claude classification (e.g. 'Senior')
    description_snippet TEXT,   -- ~500 chars
    salary_range        TEXT,
    source              TEXT,   -- domain or aggregator name
    interest_level      TEXT DEFAULT NULL
                        CHECK (interest_level IN ('interesting','not_interesting') OR interest_level IS NULL),
    notes               TEXT,
    extraction_version  TEXT,   -- hash of the prompt template used; for auditing
    date_first_seen     TEXT NOT NULL,
    date_last_seen      TEXT NOT NULL,
    date_removed        TEXT,
    is_active           INTEGER DEFAULT 1 CHECK (is_active IN (0, 1))
);

-- Scores computed at query time from area_category/level_category + user prefs.
-- No score columns stored — avoids backfill when preferences change.
-- A SQL VIEW provides composite_score for sorting:
CREATE VIEW v_jobs_scored AS
    SELECT j.*,
        -- scoring is computed in application layer; placeholder here
        0 AS composite_score
    FROM jobs j
    WHERE j.is_active = 1;

CREATE TABLE search_terms (
    id          INTEGER PRIMARY KEY,
    term        TEXT NOT NULL,
    type        TEXT NOT NULL CHECK (type IN ('company_discovery','job_search')),
    is_active   INTEGER DEFAULT 1,
    date_added  TEXT NOT NULL
);

CREATE TABLE aggregator_sources (
    id                  INTEGER PRIMARY KEY,
    name                TEXT NOT NULL,
    base_url            TEXT NOT NULL,
    search_url_template TEXT,   -- {term} and {city} placeholders
    is_enabled          INTEGER DEFAULT 0,
    notes               TEXT
);

CREATE TABLE page_fetches (
    id                  INTEGER PRIMARY KEY,
    company_id          INTEGER REFERENCES companies(id),
    url                 TEXT NOT NULL,
    fetched_at          TEXT NOT NULL,
    http_status         INTEGER,
    content_sha256      TEXT,   -- SHA-256 of raw fetched content; used for cache
    parser_strategy     TEXT,
    claude_tokens_in    INTEGER,
    claude_tokens_out   INTEGER,
    extraction_json     TEXT,   -- raw Claude output (for debugging)
    success             INTEGER CHECK (success IN (0, 1)),
    error_message       TEXT
);

CREATE TABLE config (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE scan_log (
    id            INTEGER PRIMARY KEY,
    scan_time     TEXT NOT NULL,
    scan_type     TEXT,   -- 'discovery' | 'refresh_jobs' | 'single_company'
    scope         TEXT,   -- 'global' | company domain
    status        TEXT,   -- 'running'|'completed'|'failed'|'partial'
    companies_hit INTEGER,
    jobs_found    INTEGER,
    jobs_added    INTEGER,
    jobs_removed  INTEGER,
    errors        TEXT    -- JSON array of error messages
);

-- Performance indexes
CREATE INDEX idx_jobs_company_active ON jobs(company_id, is_active);
CREATE INDEX idx_jobs_area_level     ON jobs(area_category, level_category) WHERE is_active = 1;
CREATE INDEX idx_jobs_first_seen     ON jobs(date_first_seen DESC);
CREATE INDEX idx_jobs_hash           ON jobs(hash);
CREATE INDEX idx_page_fetches_sha    ON page_fetches(content_sha256);
```

---

## 6. Aggregator Sources (Initial List)

| Source | Type | Notes |
|---|---|---|
| LinkedIn Jobs | Aggregator | Filter: Vancouver BC, Technology |
| Indeed.ca | Aggregator | Filter: Vancouver, tech keywords |
| Glassdoor | Aggregator | Filter: Vancouver |
| Workopolis | Aggregator | Canadian-focused |
| BC Tech job board | Aggregator | BC Tech Association |
| AngelList / Wellfound | Aggregator | Startup-focused |
| Company websites | Direct | Per-company careers page |

---

## 7. Out of Scope (v1)

- Email / notification alerts for new jobs
- Resume management or application tracking
- Automatic application submission
- Mobile or web version
- Multi-user / shared database
- Job salary normalization / comparison

---

## 8. Decisions Log

| # | Question | Decision |
|---|---|---|
| 1 | Hash strategy | Tiered: Tier 1 = ATS native ID, Tier 2 = normalized URL + title, Tier 3 = domain + title + location (last resort) |
| 2 | Search engine | **Brave Search API** (default, recommended). Google CSE supported as alternative but Google deprecated "search entire web" for new engines on 2026-01-20. Provider selected via `search_engine` config (`brave_search` or `google_cse`). |
| 3 | Claude CLI invocation | Subprocess via `Process.Start`; HTML via temp file; path auto-detected from PATH, overridable |
| 4 | ATS-specific parsers | Use public JSON APIs for Greenhouse, Lever, Ashby, Workable, SmartRecruiters; Claude fallback for the rest |
| 5 | Auto-refresh schedule | Manual only in v1 (discovery and job refresh are separate operations) |
| 6 | Geographic scope | Metro Vancouver by default; city list is a configurable checklist in Settings |
| 7 | Company deduplication | Deduplicate by root domain (strip `careers.`/`jobs.` subdomains before comparing) |
| 8 | Interest level inheritance | Company-level "Not Interesting" hides all its jobs (job-level overrides company-level always) |
| 9 | ORM vs. raw SQL | Dapper + hand-written SQL |
| 10 | Job description storage | Snippet only (~500 chars) in v1 |
| 11 | Aggregator sources | Pre-populated list, all **disabled by default**; user opts in per source |
| 12 | Job type defaults | Software Engineering focus by default; area and level are configurable priority lists |
| 13 | Expired postings visibility | Hidden by default; "Show Removed" checkbox toggles visibility |
| 14 | Score storage | `area_category` and `level_category` stored as raw strings; scores computed at query time in app layer — no score columns in DB |
| 15 | Global jobs view | "All Companies" entry at top of company list shows all jobs across all companies sorted by composite score |
| 16 | SQLite pragmas | WAL mode, NORMAL sync, FK enforcement on every connection open |
| 17 | HTML pre-stripping | Mandatory before Claude: remove script/style/svg/nav/footer via HtmlAgilityPack |
| 18 | Page fetch caching | `page_fetches` table caches content SHA-256; Claude skipped if content unchanged since last successful fetch |
| 19 | Headless browser | Playwright .NET used for JS-rendered pages; ATS detection determines when it's needed |
| 20 | Discovery vs. scraping cadence | Two separate operations: "Discover Companies" (weekly/on-demand) and "Refresh Jobs" (daily/on-demand) |
| 21 | Refresh safety | Failed fetches never change `is_active`; only a successful parse of a source that omits the posting triggers removal |
| 22 | LinkedIn / Indeed | Not in default source list; available to opt-in with a warning that blocking is likely |
| 23 | Score weight UI | Hidden in v1; defaults to 50/50 area/level weight hardcoded |
| 24 | Search engine setup | Brave Search: free key at https://api.search.brave.com/app/keys, paste into Settings, done. Google CSE (optional fallback): user must pre-create a Programmable Search Engine and an API key in Google Cloud Console. |
| 25 | Claude token cost | Self-managed by user; caching (Decision 18) and HTML stripping (Decision 17) reduce calls significantly |
| 26 | MVVM library | CommunityToolkit.Mvvm (source-generated, lightweight, Microsoft-maintained) |
| 27 | DI container | Microsoft.Extensions.DependencyInjection |
| 28 | Headless browser library | Playwright .NET; auto-installs browser binaries on first launch via `Playwright.Install()` |
| 29 | Build sequence | UI shell with fake/design-time data first, then data layer, then live pipeline |
| 30 | ATS slug detection | (1) Follow redirect chain from careers URL; (2) inspect final URL for known ATS domains and extract slug; (3) scan HTML for ATS script/iframe fingerprints; (4) fallback to Claude headless. User never sees this — automatic on company add. |
| 31 | Custom parser format | Compiled-in C# classes (v1); `IJobParser` interface designed to support Roslyn runtime loading in a future version |
| 32 | Settings storage | All settings in SQLite `config` table (key/value); no separate appsettings.json |
| 33 | Error/log UI | Dedicated "Refresh Log" panel/window showing `scan_log` and `page_fetches` detail; accessible from status bar |
| 34 | Levels & areas as DB tables | `levels` (1:N from jobs.level_id) and `areas` (N:N via job_areas). Closed taxonomy — classifier must map to existing entries. Editable in Settings > Categories. Sort order drives scoring priority. |
| 35 | Job → category cardinality | Each job maps to exactly one level (nullable) and 0..N areas |
| 36 | Classifier strategy | Heuristic (title keyword matching) → Claude Haiku fallback if heuristic produces no match. Claude Haiku stubbed in v1; wired in Phase 7 when Claude integration lands. Always maps to existing taxonomy entries. |
| 37 | API call tracking | `api_usage` table records daily count per provider (google_cse, ats_greenhouse, ats_lever, ats_ashby, ats_workable, ats_smartrecruiters, claude_cli, claude_haiku, bing_search). Soft caps configurable per provider (default: Google CSE 80/day below free tier, ATS APIs 500/day). Tracker raises a one-time warning when crossing cap. CLI: `usage` command shows today's counts. |
| 38 | Discovery vs. job refresh API quota | Discovery uses Brave Search (default) or Google CSE. Job refresh uses ATS APIs or direct HTTP. Quota tracker covers both. |
| 39 | Discovery URL filtering | `DomainExtractor` skips ~50 well-known non-company domains: job aggregators, social, news, B2B review sites (clutch, goodfirms, themanifest, designrush, sortlist, g2, capterra), startup directories (topstartups, getlatka, builtin), industry directories (gamecompanies, cloudtango), lead-gen scrapers (aeroleads, zoominfo), gov/edu (canada.ca, gc.ca, bcit.ca, vcc.ca, bcsc.bc.ca, etc.). Matcher uses hostname-suffix matching so `bcsc.bc.ca` is skipped without false-matching real `*.bc.ca` companies. |
| 40 | Bad-result cleanup | `companies-delete` CLI handles cleanup: by domain, by ID, `--discovered-today` (bulk), `--all` (wipe). Cascades to jobs and job_areas. |
| 41 | Rate limiting | Per-provider min-delay enforced by `RateLimiter`. Configurable via `api_min_delay_ms.{provider}`. Defaults: Brave 1100ms (free tier's 1qps), Google 500ms, ATS APIs 300ms, Claude 100ms, generic http_fetch 500ms. Threadsafe via per-provider semaphore. |
| 42 | Claude API | Direct HTTP to api.anthropic.com/v1/messages, model alias `claude-haiku-4-5`. API key in `claude_api_key` config. Used for: classifier fallback when heuristic has no match, and company profiler. Graceful degradation: empty key → service returns Source=none, profile-company errors out cleanly. |
| 43 | ATS detection slug-guess | When a careers page hints at an ATS (e.g. `data-api="ashby"`) but the slug isn't in the static HTML, guess from domain stem / no-dash variant / name-based, then verify by calling the ATS's public API. Klue → confirmed via this path. |
| 44 | Company profiles | `companies` table extended with 8 profile columns (summary, products[], industries[], tech_signals[], hq_hint, size_hint, generated_at, model). CompanyProfiler fetches homepage + /about, strips HTML (regex), sends ≤8KB to Haiku with strict-JSON schema, parses + persists. UI: double-click company in main view to open CompanyProfileWindow. |
| 45 | ATS adapters cardinality | One adapter per ATS implements `IAtsJobSource`. `JobRefresher` orchestrates: enumerate companies with detected ATS, dispatch to adapter, classify each title (heuristic→Claude), upsert with hash tier 1 (`<ats>:<native_id>`), mark previously-active-now-missing jobs as removed. |
| 46 | Field normalization at refresh boundary | ATS APIs return varied employment_type / remote_type strings ("FullTime", "Full-time", "On-Site"). `JobRefresher` normalizes to the schema's CHECK enum values before insert. |
| 47 | Self-test suite | `test` CLI runs 37 assertions across classifier, DomainExtractor, RateLimiter, migrations, pragmas. Exits 0 on pass, 1 on any failure. Reuse for CI when set up. |
| 48 | AI provider abstraction | `IAiClient` is the single interface used by classifier + profiler. Two implementations: `GeminiClient` (default) and `ClaudeClient`. `RoutingAiClient` picks at call time based on `ai_provider` config. Free tier reality drove the swap — Gemini AI Studio gives free keys with ~1000 RPD; Anthropic requires paid credit from the start. |
| 49 | Default AI provider | **Gemini** (`gemini-2.5-flash-lite` model). Switchable to Claude via Settings → AI tab. Both keys can be set simultaneously; only the active provider is called. Per-provider rate limits + soft caps tracked separately. |
| 50 | Gemini rate-limit defaults | `api_min_delay_ms.gemini = 6500ms` (≈9 RPM, well under typical 20 RPM per-minute free-tier cap). `api_soft_cap.gemini = 900` (under typical 1000 RPD ceiling). Tuned up from 4500ms after observed 429s when chaining multiple processes that share the per-minute server-side counter. |
| 51 | Playwright fallback | Both `AtsDetector` and `AiExtractedJobSource` use Playwright (`Microsoft.Playwright` 1.59) for JS-rendered pages. Chromium auto-installs via `Microsoft.Playwright.Program.Main(["install", "chromium"])` on first run. Headless mode, 30s network-idle timeout with partial-render fallback. Shared `IBrowser` instance, fresh context per fetch. |
| 52 | AI job extraction strict prompt | Tightened prompt rejects department-only entries: requires concrete role titles (level + discipline). URLs taken from page anchor list only, never invented. Empty array when nothing real on page. |
| 53 | URL cache (company_urls) | Per-company persistence of discovered URLs by kind: careers_root, department, job_list, ats_api, job_detail. Tracks last_seen, last_yielded, fail_count. Lets future refreshes skip Playwright rediscovery and target known endpoints directly. |
| 54 | Playwright network listener | PlaywrightFetcher subscribes to `page.Request` and captures every XHR/fetch URL. AtsDetector inspects the log first — catches Greenhouse/Lever/Ashby API endpoints called via JS even when invisible in static HTML. Validated live with Hootsuite (greenhouse:hootsuite caught via observed XHR). |
| 55 | JSON-LD JobPosting extractor | Parses `<script type="application/ld+json">` blocks for schema.org JobPosting data. Tried first in AiExtractedJobSource as a free path — no AI call needed when present. Walks @graph and itemListElement wrappers. |
| 56 | Cache-first JobRefresher dispatch | Decision tree: (1) native ATS if known, (2) cached job_list URLs, (3) cached department URLs (recursive 1-level crawl), (4) cached careers_root, (5) full rediscovery. Each cached URL that yields ≥1 job is marked; 2 consecutive failures auto-delete. |
| 57 | Free ATS upgrade | When the network listener observes an ATS API URL for a company without ats_type set, JobRefresher infers type+slug and updates the company. Next refresh uses the native adapter — fast and free. |
| 58 | Stale URL pruning | Auto-deletes URLs not yielding jobs in 30 days (configurable). Runs at start of RefreshAllAsync. `prune-urls [--days N]` CLI for manual cleanup. |
| 59 | Company list default view | Hides companies with 0 active jobs by default. "Show all (including 0 jobs)" checkbox in the left pane reveals them. The "All Jobs" sentinel always shows. |
| 60 | Job filter UI | Right pane has keyword text box + level dropdown + area dropdown + Clear-filters button. Filters in-memory via JobsView.Filter — updates instant. Keyword matches title/company/location/description. |
| 61 | Salary fields | `jobs.salary_min/salary_max/salary_currency/salary_period` (migration 013). Populated from Lever's `salaryRange` and schema.org JSON-LD `baseSalary`. Upsert preserves existing values when refresh returns null. JobViewModel renders as "CAD $125K–$155K/yr". |
| 62 | Bulk company seeding | `seed-companies <csv>` lets the user paste a curated list of known employers. CSV format: name,domain[,careers_url][,city][,ats_type][,ats_slug]. Skips existing domains. Most direct path to more jobs when discovery returns noisy results. |

## 9. Remaining Open Questions

1. **`extraction_version` format:** Recommend SHA-256 of the prompt template text so it auto-updates when the prompt changes. Stored alongside Claude model ID (e.g. `claude-sonnet-4-6`) in `page_fetches`.

## 10. Build Plan

Build sequence (each phase produces something runnable):

| Phase | Deliverable | Status |
|---|---|---|
| 1 | Solution structure, project layout, DI wiring, DB initialization with schema | ✅ done |
| 2 | WPF UI shell: company list + job pane + All Jobs view + Settings skeleton, all driven by fake/design-time data | ✅ done |
| 2.5a | CLI scaffolding with file mirror + parent-console attach; `db-info`, `migrate` | ✅ done |
| 2.5b | `ConfigRepository`, `AggregatorRepository`, Settings window with tabbed UI | ✅ done |
| 3 | Data layer: Dapper repos for companies, jobs, scan_log, config; `SqliteJobDataService` replaces fake at runtime | ✅ done |
| 3.5 | Levels/areas as DB tables, `job_areas` junction, scoring against tables; Settings Categories tab | ✅ done |
| 3.6 | Heuristic classifier + Claude Haiku fallback stub; `classify` CLI command | ✅ done |
| 4 | Company discovery via **Brave Search** (Google CSE fallback); `discover` CLI; `DomainExtractor` skip-list; auth/quota fast-abort | ✅ done |
| 4.5 | API usage tracker (`api_usage` table) with per-provider soft caps; `usage` CLI; warning event when caps cross | ✅ done |
| 4.6 | Rate limiter (per-provider min-delay); self-test CLI (`test`) with 37 assertions; `companies-delete` cleanup CLI | ✅ done |
| 5 | ATS detection: HTTP fetch + redirect follow + HTML fingerprint + slug-guess-and-verify against ATS API. Supports Greenhouse / Lever / Ashby / Workable / SmartRecruiters / Recruitee. `detect-ats <domain> \| --all \| --missing` | ✅ done |
| 6 | AI client (Gemini default, Claude optional) via `IAiClient` + `RoutingAiClient`; `AiFallbackClassifier` with closed-taxonomy prompts; `test-ai` CLI | ✅ done |
| 6.5 | Company profiler: HtmlTextExtractor + Haiku summarizes homepage/about; CompanyProfile model + 8 new DB columns; `profile-company <domain> \| --all-missing` CLI; CompanyProfileWindow opened via double-click | ✅ done |
| 6.6 | ATS API adapters: Greenhouse, Lever, Ashby. `IJobRefresher` orchestrates fetch + classify + upsert + mark-removed; `refresh-jobs [--company X]` CLI; GUI Refresh Jobs button wired | ✅ done |
| 7 | Playwright headless browser for non-API ATS detection (DOM-rendered fingerprint matching); `AiExtractedJobSource` parses arbitrary careers pages via Playwright + AI; `parse-page` CLI; `JobRefresher` fallback dispatch | ✅ done |
| 7.5 | URL cache (company_urls); Playwright network listener captures XHR/fetch; JSON-LD JobPosting extractor; cache-first JobRefresher dispatch with recursive 1-level crawl; auto-prune; free ATS upgrade when network listener catches API endpoint | ✅ done |
| 8 | Refresh pipeline polish: scheduled background runs, scan_log UI, page_fetches cache for Claude calls |  |
| 9 | Right-click context menus (Mark Interesting / Open / Copy) ✓; click-to-open-in-browser on jobs ✓; filter UI |  |
| 10 | Filter UI for area/level multiselect, interest level dropdown, search across descriptions; status bar polish |  |

### CLI command reference (current)

| Command | Purpose |
|---|---|
| `db-info` | DB path, table list with row counts, applied migrations |
| `migrate` | Run pending migrations (idempotent) |
| `config-list` | List all config keys |
| `config-get <key>` | Read one config value |
| `config-set <key> <value>` | Set/update a config value |
| `levels-list` | List all levels with sort order |
| `areas-list` | List all areas with sort order |
| `classify "<title>" [--dept X]` | Run the classifier on a job title (heuristic + Claude Haiku fallback) |
| `companies-list [--ats X] [--limit N]` | Tabular list of companies |
| `companies-add <name> <domain> [--city X] [--careers URL]` | Manually add a company |
| `companies-delete <domain> \| --id N \| --discovered-today \| --all` | Delete companies (cascades to jobs/areas) |
| `jobs-list [--company X] [--show-removed] [--limit N]` | List jobs sorted by composite score |
| `discover [--pages N]` | Run company discovery via the configured search engine |
| `usage` | Show today's API call counts vs. soft caps |
| `seed-fake` | Populate DB with fake test data (idempotent) |
| `test` | Run the self-test suite (classifier + DomainExtractor + RateLimiter + migrations) — exit 0/1 |
| `test-ai` | Verify the configured AI provider (Gemini or Claude) with a tiny round-trip |
| `parse-page <url>` | Render a careers page with Playwright + AI-extract jobs (Phase 7) |
| `company-urls <domain> [--kind X]` | Inspect the per-company URL cache (careers_root, department, job_list, ats_api, job_detail) |
| `prune-urls [--days N]` | Delete cached URLs that haven't yielded jobs in N days (default 30) |
| `seed-companies <csv>` | Bulk-import companies from a CSV (name,domain[,careers_url][,city][,ats_type][,ats_slug]) |
| `detect-ats <domain> \| --all \| --missing` | Detect which ATS a company uses (Greenhouse/Lever/Ashby/Workable/SmartRecruiters/Recruitee) |
| `profile-company <domain> \| --all-missing` | Generate a Claude Haiku company profile from homepage + /about |
| `refresh-jobs [--company X]` | Fetch jobs from ATS APIs and upsert; auto-classify on insert |
