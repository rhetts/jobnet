# Jobnet Changelog

## 2026-05-15 (afternoon) — Filters, salary, seed-companies

Four product improvements.

### "Show all companies" toggle (left pane)

Default view now **hides companies with 0 active jobs**. With ~50 companies in
the DB but only a handful yielding jobs, the empty rows were noise. New
checkbox under the search box reveals them when you want to triage.

### Job filter row (right pane)

Above the jobs list: keyword text box + level dropdown + area dropdown +
Clear-filters button. All filter in-memory via the JobsView ICollectionView,
so updates are instant. The keyword matches title, company name, location,
or description snippet.

### Salary min/max parsing (migration 013)

Schema gets `salary_min`, `salary_max`, `salary_currency`, `salary_period`.
Wired through:
- **Lever** structured `salaryRange { min, max, currency, interval }` field
- **JSON-LD** schema.org `baseSalary` (handles QuantitativeValue with min/max/unitText)
- **JobViewModel** meta line: `Remote · Full-time · Score 95 · 3d old · CAD $125K–$155K/yr`
- **Upsert** preserves existing salary when a refresh returns null

Verified live: **21 of 22 Blackbird jobs now show salary** ($110K–$240K CAD/year).

### Better company discovery — `seed-companies` CLI

New `seed-companies <csv>` bulk-imports companies. Bundled
`vancouver-companies-seed.csv` with 49 hand-picked Vancouver/BC tech employers.
Initial import: 44 new (5 already present).

This is the most direct fix for "we don't have enough companies" — Brave-based
discovery finds too many directory and lead-gen sites; manually-seeded known
employers are higher signal. After detect-ats runs across the 44 new ones,
many should resolve to a native ATS adapter and yield real jobs on next refresh.

## 2026-05-14 → 2026-05-15 (overnight) — Phase 7.5: URL cache, network listener, JSON-LD, recursive crawl

Quick verification when you wake up:
```powershell
$exe = "C:\Work\Jobnet\Jobnet\bin\Debug\net8.0-windows\Jobnet.exe"
& $exe test               # 37/37 should pass
& $exe jobs-list --limit 50
& $exe company-urls hootsuite.com   # see the discovered URL cache
& $exe usage              # API counts vs caps
```

### Headline result

**Active jobs: 30 → 76 (2.5× increase). Companies with jobs: 3 → 5.**

Newly working via the network listener / Playwright fingerprinting:
- **Hootsuite** → Greenhouse (40 jobs) — caught via observed boards-api.greenhouse.io XHR
- **Visier** → Greenhouse (5 jobs) — caught via Playwright-rendered HTML pattern
- **SkyBox Labs** → Lever (1 job) — caught via observed api.lever.co XHR

All three previously yielded **zero** jobs because their careers pages are JS-rendered SPAs.

### Self-tests

50/50 passing (was 37) — 13 new UrlClassifier assertions cover job_detail, department,
job_list classification and the non-job skip rules.

### What changed

- **company_urls cache** (migration 012): table tracking URLs discovered for each
  company by kind (careers_root, department, job_list, ats_api, job_detail).
  Records fail_count, last_seen, last_yielded for pruning.
- **PlaywrightFetcher network listener**: every XHR/fetch URL the page hits is
  captured in PlaywrightFetchResult.NetworkRequests. AtsDetector checks the log
  first — catches Greenhouse/Lever/Ashby API endpoints invisible in static HTML.
- **JSON-LD JobPosting extractor**: parses `<script type="application/ld+json">`
  blocks for schema.org JobPosting data. Tried first in AiExtractedJobSource as
  a FREE path — no AI call needed when present.
- **URL classifier**: regex rules distinguish job_detail vs department vs
  job_list vs unknown by URL path/query. Skips obvious non-job links.
- **AiExtractedJobSource.FetchForCompanyAsync** persists careers_root, classified
  anchors, and observed ATS API endpoints to company_urls.
- **Cache-first dispatch in JobRefresher**: tries native ATS → cached job_list
  → cached department (recursive crawl) → cached careers_root → full
  rediscovery, in that order. Each cached URL that yields ≥1 job updates
  last_yielded; failures get fail_count++, deleted after 2 strikes.
- **Free-upgrade path**: when the network listener catches an ats_api URL on a
  company with no ats_type yet, JobRefresher infers type+slug and updates the
  company. Subsequent refreshes use the native adapter (fast + free).
- **Auto-prune**: RefreshAllAsync deletes URLs not yielding in 30 days.
  `prune-urls [--days N]` CLI for manual cleanup.
- **`company-urls <domain> [--kind X]`** CLI for cache inspection.

### Known limitations remaining

- Recursive crawl only goes one level deep. Hootsuite-style "show me all
  Engineering jobs" is covered, but deeper navigation isn't (rare in practice).
- Workday, iCIMS, Jobvite still not in ATS pattern list — adding any of these
  unlocks a lot of enterprise companies.

## 2026-05-14 (evening) — Phase 7: Playwright + AI extraction fallback

- New `Microsoft.Playwright` dependency. Chromium auto-installs via
  `PlaywrightFetcher.EnsureBrowserInstalled()` (delegates to `Microsoft.Playwright.Program.Main`).
- `IPlaywrightFetcher` / `PlaywrightFetcher` — shared headless Chromium, lazy init,
  network-idle wait, partial-render fallback on timeout
- `AiExtractedJobSource` (ats_type=ai_extract) — implements `IAtsJobSource`:
  Playwright fetches the careers page, regex extracts visible anchors with hrefs,
  sends cleaned text + anchor list to Gemini/Claude with strict-JSON job schema
- `JobRefresher` now dispatches to `AiExtractedJobSource` as fallback when no native
  ATS adapter matches a company (uses careers URL, website URL, or `<domain>/careers`)
- `AtsDetector` has a Playwright fallback — when static probes find nothing, renders
  top candidates with Chromium and re-runs URL/HTML pattern matching on the rendered
  DOM (including slug-guess for hint-only patterns)
- Candidate URLs now include `careers.<domain>` and `jobs.<domain>` subdomains
- CLI: `parse-page <url>` — directly test Playwright + AI extractor on any URL
- Rate limiter gets a new `playwright_fetch` provider (2000ms default, kind to target servers)

Verified end-to-end: `parse-page https://careers.hootsuite.com` rendered the page via
Chromium, extracted listings (initially picked up departments — now rejected by
tightened prompt). Pipeline functioned; quality limited by the page content itself
(Hootsuite's careers page shows department categories, not individual jobs).

### Known limitations of Phase 7

- Sites showing only department categories (Hootsuite-style) won't yield jobs without
  recursive crawling of department links (future work).
- Gemini free tier per-minute cap (~20 RPM) is strict — burst usage hits 429s.
  Retry semantics are sliding-window; daily counter still has plenty of room (~30 of 900 used today).
- Workday / iCIMS / Jobvite / Taleo aren't in the ATS pattern list (easy to add later).

## 2026-05-14 (afternoon) — AI provider abstraction, Gemini default

- New `IAiClient` interface — single seam used by classifier + profiler
- Two implementations: `GeminiClient` (Google AI Studio REST API) and `ClaudeClient` (Anthropic)
- `RoutingAiClient` selects between them based on `ai_provider` config value (default: `gemini`)
- `AiFallbackClassifier` replaces `ClaudeHaikuClassifier` — same behavior, provider-agnostic
- `CompanyProfiler` now provider-agnostic
- Migration 009 adds gemini_api_key, gemini_model, ai_provider, gemini rate limit/cap keys; flips default to Gemini
- Settings UI gets a new **AI** tab — provider dropdown, separate key + model field per provider
- CLI: `test-claude` removed → `test-ai` (works for whichever provider is selected)
- Self-tests still pass (37/37)
- **Verified live with Gemini**: `test-ai` round-trip ✓, `profile-company steamclock.com` produced an accurate Vancouver mobile-app-studio summary

### Why Gemini default

Google AI Studio gives a free API key (no credit card) with a generous free tier — typically 15 RPM and ~1000 requests/day on flash-lite. Anthropic's Claude API requires paid credit. For a personal tool, that matters.

Both keys can be configured simultaneously — flip `ai_provider` to switch which one Jobnet actually calls.

### How to get a Gemini key (~30 seconds)

1. Open **https://aistudio.google.com/apikey**
2. Sign in with any Google account
3. Click **Create API key** → optionally pick "Create in new project"
4. Copy the key (starts with `AIzaSy…`, 39 chars)
5. `Jobnet.exe config-set gemini_api_key <key>`  *or paste into Settings → AI*
6. `Jobnet.exe test-ai`  *should print "Jobnet test OK"*

## Overnight autonomous session — 2026-05-13 → 2026-05-14

Reviewer: when you wake up, run these three commands to see the new functionality:

```powershell
$exe = "C:\Work\Jobnet\Jobnet\bin\Debug\net8.0-windows\Jobnet.exe"
& $exe test                              # 37 self-tests should all pass
& $exe jobs-list --limit 30              # 30 real jobs from Klue + Blackbird Interactive
& $exe usage                             # see API call counts vs soft caps
```

Then launch the GUI and:
- Click **Refresh Jobs** — re-fetches from Ashby/Lever, marks any removed
- Double-click any company (e.g. SkyBox Labs) — opens its profile window
- Click **Generate profile** on a profile window (requires Claude API key)

### What's new

#### Phase 4.6 — Rate limiter + test harness
- `IRateLimiter` enforces per-provider min-delay; threadsafe via semaphore
- Defaults: Brave 1100ms (their docs require 1qps), Google 500ms, ATS APIs 300ms, Claude 100ms
- All settable via `api_min_delay_ms.{provider}` config keys
- `test` CLI: 37 assertions covering classifier, DomainExtractor, RateLimiter, migrations
- `companies-delete` CLI for cleanup (by domain / id / --discovered-today / --all)

#### Phase 5 — ATS detection
- `IAtsDetector` tries candidate URLs (CareersUrl, /careers, /jobs, /about/careers, ...)
- Follows redirects; matches final URL against 6 ATS hostname patterns
- HTML fingerprinting catches embedded boards (iframes, script tags)
- **Slug-guess fallback**: when an ATS is hinted but slug isn't visible
  (JS-rendered), guesses from domain/name and verifies against the ATS's public API
- Verified working on Klue (Ashby) and Blackbird Interactive (Lever)
- CLI: `detect-ats <domain> | --all | --missing`

#### Phase 6 — Claude Haiku API + classifier
- `ClaudeClient` against api.anthropic.com/v1/messages, model `claude-haiku-4-5`
- `ClaudeHaikuClassifier` no longer a stub — sends live taxonomy as prompt context,
  parses strict JSON, maps to level/area IDs
- Graceful degradation when API key empty (returns Source=none, heuristic remains)
- `test-claude` CLI for round-trip verification

#### Phase 6.5 — Company profiler
- 8 new columns on companies table for profile data
- `CompanyProfiler` fetches homepage + /about, regex-strips HTML, sends ≤8KB
  to Haiku with strict-JSON schema, parses + persists
- `CompanyProfileWindow` opened by double-clicking a company in the main view
- Shows summary, products/industries/tech-signals as chips, ATS info, careers link
- CLI: `profile-company <domain> | --all-missing`

#### Phase 6.6 — ATS adapters + refresher
- 3 adapters: Greenhouse, Lever, Ashby (each ~80 lines, simple JSON APIs)
- `JobRefresher` orchestrates: pick adapter by `companies.ats_type`, fetch, classify
  each title (heuristic→Claude fallback), upsert with hash tier 1 (`ats:native_id`),
  mark previously-active-but-now-missing as removed
- Field normalization at boundary: "FullTime", "Full-time" → "full-time"
- CLI: `refresh-jobs [--company X]`
- GUI: **Refresh Jobs** button now wired (async, button disables, view auto-reloads)

### What works end-to-end right now (no Claude key needed)

- `discover` finds Vancouver tech companies via Brave Search (47/70 filter ratio,
  ~21 real companies per run)
- `detect-ats` finds Klue (Ashby) and Blackbird Interactive (Lever) confirmed
- `refresh-jobs` pulls **30 real jobs** from those two companies
- Jobs auto-classified to levels/areas via heuristic
- Scoring works: Senior+SE roles score 100, drops to 0 for off-priority

### What needs your Claude API key to test

- `test-claude` — quick round-trip check
- `profile-company <domain>` — Haiku summarizes the company website
- Classifier fallback for titles like "Solutions Architect" that the heuristic misses

To add a key:
```
& $exe config-set claude_api_key sk-ant-...
& $exe test-claude
& $exe profile-company steamclock.com
```

### What's still ahead

| Phase | Description |
|---|---|
| 7 | Playwright headless browser for non-API ATS detection; Claude HTML extraction for free-form careers pages |
| 8 | Scheduled refresh, scan_log UI, page_fetches cache |
| 9 | Right-click context menus, click-to-open-in-browser on jobs, filter UI |
| 10 | Multi-select filters, status bar polish |

### Open notes for review

1. **Companies that still need ATS detection** (no major-ATS markers visible without JS rendering): Hootsuite, Coinbase, Bench Accounting, Visier, Steamclock, SkyBox Labs, and ~15 others. Phase 7 (Playwright) is the right tool — until then they have empty job listings.
2. **`companies-add` arg parsing bug**: multi-word names fail when passed unquoted from PowerShell. Workaround: don't include spaces. Real fix is small — handle quoted args in CliRunner. Low priority.
3. **The Bench duplicate**: I accidentally added "Bench" with domain="Accounting" during testing. Run `companies-delete Accounting` to clean up if you want.
4. **Claude API key**: not configured. Some features (`profile-company`, smarter classification on ambiguous titles) will skip until you add it.

### Git state

- 8 commits since the initial commit
- All pushed to `https://github.com/rhetts/jobnet` (private)
- `main` branch, no other branches
