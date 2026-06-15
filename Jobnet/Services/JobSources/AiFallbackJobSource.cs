using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Jobnet.Services.Ai;
using Jobnet.Services.Parsing;
using Jobnet.Services.Playwright;
using Jobnet.Services.Profiling;

namespace Jobnet.Services.JobSources;

/// <summary>
/// Fallback job source for companies that don't use a known ATS. Renders the careers page with
/// Playwright, sends the text + visible anchor links to the AI client, and parses a strict-JSON
/// job list out of the response. Also persists discovered URLs to the company_urls cache.
/// </summary>
public sealed class AiFallbackJobSource : IJobSource
{
    public string AtsType => "ai_extract";

    private readonly IPlaywrightFetcher _fetcher;
    private readonly IAiClient _ai;
    private readonly ICompanyUrlsRepository _urls;
    private readonly IAiExtractionCacheRepository _cache;
    private readonly IConfigRepository _config;
    private readonly SelectorProfileReplayer _selectorParser;
    private readonly AiSelectorDeriver _selectorDeriver;
    private readonly ICompanyRepository _companies;
    private readonly Parsing.HtmlPatternParsers.HtmlPatternRegistry _companyParsers;
    private readonly Jobnet.Services.Logging.IRunLogger _runs;

    public AiFallbackJobSource(IPlaywrightFetcher fetcher, IAiClient ai, ICompanyUrlsRepository urls,
                                  IAiExtractionCacheRepository cache, IConfigRepository config,
                                  SelectorProfileReplayer selectorParser, AiSelectorDeriver selectorDeriver,
                                  ICompanyRepository companies,
                                  Parsing.HtmlPatternParsers.HtmlPatternRegistry companyParsers,
                                  Jobnet.Services.Logging.IRunLogger runs)
    {
        _fetcher = fetcher;
        _ai = ai;
        _urls = urls;
        _cache = cache;
        _config = config;
        _selectorParser = selectorParser;
        _selectorDeriver = selectorDeriver;
        _companies = companies;
        _companyParsers = companyParsers;
        _runs = runs;
    }

    /// <summary>Name of the hand-written parser that handled the most recent fetch, or null when
    /// the path went through cached selectors / JSON-LD / AI extract instead. Exposed for
    /// JobRefresher's per-step logging so the user can see which pattern matched on each company.</summary>
    public string? LastParserUsed { get; private set; }

    /// <summary>True when the selector-parser feature flag is on in config. Read each call so
    /// the user can flip it from Settings without restarting.</summary>
    private bool SelectorParserEnabled =>
        string.Equals(_config.GetOrDefault("selector_parser_enabled", "true"), "true",
                       StringComparison.OrdinalIgnoreCase);

    /// <summary>Cache hit count for the most recent FetchForCompanyAsync call (across all AI
    /// extractions invoked). Exposed so callers like JobRefresher can surface this in run logs.</summary>
    public int LastCacheHits { get; private set; }

    /// <summary>Fetch with company context. Persists discovered URLs (anchors + ATS API endpoints
    /// observed in the network log) to company_urls so future refreshes can skip rediscovery.</summary>
    public async Task<IReadOnlyList<RawJobPosting>> FetchForCompanyAsync(int companyId, string url, CancellationToken ct = default)
    {
        var company = companyId > 0 ? _companies.GetById(companyId) : null;
        return await FetchForCompanyAsync(company, url, ct);
    }

    /// <summary>Company-aware overload that takes the full <see cref="Company"/> so we can read
    /// (and write) its parser-strategy state without an extra DB round-trip. JobRefresher uses
    /// this form; the CLI parse-page path still calls the int overload (with companyId=0 →
    /// company=null for the no-persistence case).</summary>
    public async Task<IReadOnlyList<RawJobPosting>> FetchForCompanyAsync(Company? company, string url, CancellationToken ct = default)
    {
        var jobs = await FetchOnceAsync(company, url, ct);
        if (jobs.Count > 0 || company is null) return jobs;

        // The starting URL was probably a marketing landing page that lists categories instead
        // of postings (common on WordPress-style /about/careers pages). The anchor scan just
        // discovered the real job board — follow the freshest JobList URL once and re-extract.
        // Depth is capped at one extra hop; we never recurse into this branch again.
        var followups = _urls.GetByCompany(company.Id)
            .Where(u => u.Kind == UrlKind.JobList
                     && !string.Equals(u.Url, url, StringComparison.OrdinalIgnoreCase)
                     && u.FailCount < 3)
            .OrderBy(u => u.FailCount)
            .ThenByDescending(u => u.LastSeen)
            .Take(2)
            .ToList();

        foreach (var fu in followups)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var more = await FetchOnceAsync(company, fu.Url, ct);
                if (more.Count > 0) return more;
                _urls.RecordFailure(company.Id, fu.Url);
            }
            catch
            {
                _urls.RecordFailure(company.Id, fu.Url);
            }
        }
        return jobs;
    }

    /// <summary>Single-shot fetch + extract, no follow-up logic. Persists discovered URLs and
    /// marks the source URL as yielded when jobs come back.
    ///
    /// Order of attempts (cheapest first):
    ///   1. Cached selector profile (deterministic, no AI cost) — when company has one and
    ///      the global flag is on.
    ///   2. JSON-LD structured data extraction (free).
    ///   3. Hand-written company parsers (HtmlPatternRegistry) — pattern-specific extractors
    ///      that handle known shapes (Lever shortcode, direct Greenhouse links, etc.).
    ///   4. AI extraction. If it yields jobs AND no selector profile is cached yet, derive one
    ///      from the same HTML and persist it so the next refresh skips step 4 entirely.</summary>
    private async Task<IReadOnlyList<RawJobPosting>> FetchOnceAsync(Company? company, string url, CancellationToken ct)
    {
        var companyId = company?.Id ?? 0;
        LastParserUsed = null;

        // ── URL-level recency skip ──────────────────────────────────────────────
        // Cheap pre-check before Playwright: if we scraped this URL within the configured
        // skip window, just return the cached jobs and avoid the entire Playwright + AI
        // roundtrip. Default 0 (disabled). 6-12h is a sane setting for production — most
        // careers pages don't churn faster than that.
        //
        // Bias: this trades freshness for cost. A page that adds a new posting within the
        // window is missed until the window expires. Acceptable for AI-extract companies
        // (which by definition aren't on a structured ATS) but the user can tune per
        // their patience.
        var skipHours = int.TryParse(_config.GetOrDefault("ai_extraction_url_skip_hours", "0"),
                                      out var sh) ? sh : 0;
        if (skipHours > 0)
        {
            var recent = _cache.GetByUrlIfRecent(url, skipHours);
            if (recent is not null)
            {
                LastCacheHits++;
                // No SetLastScan / MarkYielded here — those happen in the outer caller (JobRefresher)
                // based on returned jobs. The whole point of this skip is to take a fast lane
                // before any side effects.
                return DeserializeCached(recent);
            }
        }

        var fetch = await _fetcher.FetchAsync(url, ct);
        PersistDiscoveredUrls(companyId, fetch);

        if (!fetch.Success || string.IsNullOrEmpty(fetch.Html))
            throw new InvalidOperationException(fetch.Error ?? "Playwright fetch failed");

        // 1) Cached selector profile. Skip when globally disabled, per-company disabled, or no
        //    profile cached yet.
        if (company is not null
            && SelectorParserEnabled
            && !company.ParserStrategyDisabled
            && !string.IsNullOrWhiteSpace(company.ParserStrategy))
        {
            try
            {
                var selectorJobs = _selectorParser.Parse(company.ParserStrategy!, fetch.Html, fetch.FinalUrl);
                if (selectorJobs.Count > 0)
                {
                    _companies.SetParserStrategyResult(company.Id, "ok", DateTime.UtcNow, errorMessage: null);
                    _urls.MarkYielded(company.Id, fetch.FinalUrl);
                    return selectorJobs;
                }

                // 0 jobs from a previously-working profile = likely drift. Clear so the AI path
                // below re-derives. Mark the result so the Parser Report screen surfaces it.
                _companies.SetParserStrategyResult(company.Id, "drift", DateTime.UtcNow,
                    errorMessage: "Cached selectors returned 0 jobs; re-deriving.");
                _companies.ClearParserStrategy(company.Id);
                company.ParserStrategy = null;   // keep the in-memory model consistent for the rest of this call
            }
            catch (SelectorReplayException ex)
            {
                _companies.SetParserStrategyResult(company.Id, "error", DateTime.UtcNow, errorMessage: ex.Message);
                _companies.ClearParserStrategy(company.Id);
                company.ParserStrategy = null;
            }
        }

        // 2) JSON-LD structured data (free, no AI cost).
        var jsonLd = JsonLdJobExtractor.Extract(fetch.Html, fetch.FinalUrl);
        if (jsonLd.Count > 0)
        {
            if (companyId > 0) _urls.MarkYielded(companyId, fetch.FinalUrl);
            return jsonLd;
        }

        // 3) Hand-written company parsers. Probe the registry; any positive match runs that
        //    parser and we skip the AI call. Hard failures inside Parse are caught here so a
        //    buggy parser doesn't poison the refresh — we just fall through to AI extraction.
        var handParser = _companyParsers.ResolveFor(fetch.FinalUrl, fetch.Html);
        if (handParser is not null)
        {
            try
            {
                var parsed = handParser.Parse(fetch.Html, fetch.FinalUrl);
                if (parsed.Count > 0)
                {
                    LastParserUsed = handParser.Name;
                    if (companyId > 0)
                    {
                        _urls.MarkYielded(companyId, fetch.FinalUrl);
                        _companies.SetLastCompanyParser(companyId, handParser.Name);
                        if (company is not null) company.LastCompanyParser = handParser.Name;
                    }
                    return parsed;
                }
                // 0 jobs from a positive CanHandle is suspicious but not fatal — fall through
                // to AI extraction. Could indicate the page genuinely has no openings today.
            }
            catch (Exception ex)
            {
                Jobnet.Services.Ai.AiLogger.LogParseFailure(
                    taskTag: $"company_parser:{handParser.Name}",
                    exception: ex,
                    rawResponse: fetch.Html.Length > 4000 ? fetch.Html[..4000] : fetch.Html,
                    extraContext: $"url={fetch.FinalUrl}");
                // Fall through to AI extract.
            }
        }

        if (!_ai.IsConfigured)
            throw new InvalidOperationException("Page has no JSON-LD job data and AI client not configured");

        // 3) AI extraction (most expensive). On success, derive a selector profile so future
        //    refreshes skip this step.
        var jobs = await ExtractViaAiAsync(fetch, ct);
        if (jobs.Count > 0 && companyId > 0) _urls.MarkYielded(companyId, fetch.FinalUrl);

        if (jobs.Count > 0
            && company is not null
            && SelectorParserEnabled
            && !company.ParserStrategyDisabled
            && string.IsNullOrWhiteSpace(company.ParserStrategy)
            && !IsInDeriveBackoff(company))
        {
            await TryDeriveAndPersistProfileAsync(company, fetch.Html, fetch.FinalUrl, ct);
        }
        return jobs;
    }

    /// <summary>True when the last derivation attempt for this company errored within the backoff
    /// window. Prevents a permanently-failing deriver (e.g. local LLama too small for the prompt)
    /// from burning an AI call on every refresh.</summary>
    private bool IsInDeriveBackoff(Company company)
    {
        if (company.ParserStrategyLastResult != "error") return false;
        if (company.ParserStrategyLastResultAt is not { } lastAt) return false;

        var hours = int.TryParse(_config.GetOrDefault("selector_deriver_backoff_hours", "24"), out var h) && h > 0
                    ? h : 24;
        return (DateTime.UtcNow - lastAt).TotalHours < hours;
    }

    /// <summary>Best-effort: ask the deriver for a selector profile from the same HTML the AI
    /// just extracted from. Persist when the deriver passes its own sanity check. Failures are
    /// recorded but never abort the refresh — we already have jobs from the AI pass.</summary>
    private async Task TryDeriveAndPersistProfileAsync(Company company, string html, string baseUrl, CancellationToken ct)
    {
        try
        {
            var derived = await _selectorDeriver.DeriveAsync(html, baseUrl, ct);
            if (derived.Success && derived.ProfileJson is not null)
            {
                _companies.SetParserStrategy(company.Id, derived.ProfileJson, DateTime.UtcNow);
                company.ParserStrategy = derived.ProfileJson;
                company.ParserStrategyDerivedAt = DateTime.UtcNow;
            }
            else
            {
                _companies.SetParserStrategyResult(company.Id, "error", DateTime.UtcNow,
                    errorMessage: derived.Error ?? "Derivation produced no usable profile.");
            }
        }
        catch (Exception ex)
        {
            _companies.SetParserStrategyResult(company.Id, "error", DateTime.UtcNow, errorMessage: ex.Message);
        }
    }

    private void PersistDiscoveredUrls(int companyId, PlaywrightFetchResult fetch)
    {
        if (companyId <= 0) return;

        // The landing URL itself
        if (!string.IsNullOrEmpty(fetch.FinalUrl))
            _urls.Upsert(companyId, fetch.FinalUrl, UrlKind.CareersRoot, label: null, discoveredVia: "anchor_scan");

        // ATS API endpoints observed in network requests
        foreach (var req in fetch.NetworkRequests)
        {
            if (req.Url.Contains("boards-api.greenhouse.io/v1/boards") ||
                req.Url.Contains("api.lever.co/v0/postings") ||
                req.Url.Contains("api.ashbyhq.com/posting-api/job-board") ||
                req.Url.Contains("workable.com/api/") ||
                req.Url.Contains("smartrecruiters.com/v1/companies"))
            {
                _urls.Upsert(companyId, req.Url, UrlKind.AtsApi, label: null, discoveredVia: "network_listener");
            }
        }

        // Anchors classified by URL pattern
        if (!string.IsNullOrEmpty(fetch.Html))
        {
            foreach (var (text, href) in ExtractAnchors(fetch.Html, fetch.FinalUrl, max: 200))
            {
                var kind = UrlClassifier.Classify(href);
                if (kind is null) continue;
                _urls.Upsert(companyId, href, kind, label: text, discoveredVia: "anchor_scan");
            }
        }
    }

    /// <summary>Slug here is the URL to fetch. Used by parse-page CLI (no company persistence).</summary>
    public async Task<IReadOnlyList<RawJobPosting>> FetchAsync(string slug, CancellationToken ct = default)
        => await FetchForCompanyAsync(0, slug, ct);

    private async Task<IReadOnlyList<RawJobPosting>> ExtractViaAiAsync(PlaywrightFetchResult fetch, CancellationToken ct)
    {
        var text = HtmlTextExtractor.Extract(fetch.Html, maxChars: 10_000);
        var anchors = ExtractAnchors(fetch.Html, fetch.FinalUrl, max: 80);

        if (anchors.Count == 0 && string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Page rendered but text content is empty");

        var anchorBlock = anchors.Count == 0 ? "(none)" :
            string.Join("\n", anchors.Take(80).Select(a => $"- [{a.Text}] → {a.Href}"));

        // The content that COULD be sent to the AI in the worst case. Hashing it lets us return
        // cached jobs when the page hasn't actually changed since the last AI extraction.
        var fullContentToAi = text + "\n--ANCHORS--\n" + anchorBlock;
        var contentHash = ShortHash(fullContentToAi);
        var ttlHours = int.TryParse(_config.GetOrDefault("ai_extraction_cache_ttl_hours", "168"),
                                     out var t) ? t : 168;

        var cached = _cache.GetIfFresh(fetch.FinalUrl, contentHash, ttlHours);
        if (cached is not null)
        {
            LastCacheHits++;
            return DeserializeCached(cached);
        }

        var system =
            "You extract ACTUAL job postings from a company's careers page. Output STRICT JSON only — no prose, no markdown.\n" +
            "Schema:\n" +
            "{ \"jobs\": [\n" +
            "  { \"title\": \"...\", \"url\": \"<absolute URL or null>\", \"location\": \"...\", \"remote_type\": \"on-site|hybrid|remote|unknown\", \"employment_type\": \"full-time|part-time|contract|unknown\", \"department\": \"...\", \"source_span\": \"<verbatim ≤120-char snippet from the input where this title appears>\" }\n" +
            "] }\n" +
            "\n" +
            "Rules:\n" +
            "- A job title is SPECIFIC: it names a role (e.g. 'Senior Backend Engineer', 'Product Designer'), not a category.\n" +
            "- REJECT department names alone ('Engineering', 'Marketing', 'Sales', 'Product', 'Operations'). These are NOT jobs.\n" +
            "- REJECT navigation items, team pages, locations, or 'view all jobs' links.\n" +
            "- A real job title usually has both a seniority/level word AND a discipline word, or names a specific position.\n" +
            "- Use only the anchor list for URLs — never invent. Skip non-job anchors (privacy, about, blog, etc.).\n" +
            "- source_span MUST be copied VERBATIM from the input — same casing, same spelling, same punctuation. It is how we verify the title is real. If you can't find the title in the input, omit the row.\n" +
            "- If the page only shows department/category filters with no individual postings visible, output {\"jobs\": []}.";

        // Two-pass extraction. First pass sends ONLY the anchor list — for the typical careers
        // page where each job is an <a> with the title as the link text, this is sufficient and
        // cuts the prompt from ~3K tokens to a few hundred. We only fall through to the full-text
        // pass when the cheap pass returns nothing useful.
        IReadOnlyList<RawJobPosting> jobs = System.Array.Empty<RawJobPosting>();
        if (anchors.Count >= 3)
        {
            var firstUser =
                $"Source: {fetch.FinalUrl}\n\n" +
                $"Anchors found on page (label → href):\n{anchorBlock}";
            var firstResp = await _ai.CompleteAsync(firstUser, system, maxTokens: 2048, ct, task: "extraction");
            jobs = ParseJobs(firstResp.Text, fetch.FinalUrl);
        }

        // Second pass: full text + anchors. Runs when the anchors-only pass returned nothing (page
        // may list job titles in body text, not links), or when the page had too few anchors to bother.
        if (jobs.Count == 0 && !string.IsNullOrWhiteSpace(text))
        {
            var secondUser =
                $"Source: {fetch.FinalUrl}\n\n" +
                $"Page text (cleaned):\n{text}\n\n" +
                $"Anchors found on page (label → href):\n{anchorBlock}";
            var secondResp = await _ai.CompleteAsync(secondUser, system, maxTokens: 2048, ct, task: "extraction");
            jobs = ParseJobs(secondResp.Text, fetch.FinalUrl);
        }

        // Citation verification: every job title must appear verbatim in the source we fed the
        // model (the rendered text + anchor labels). This is a hard guard against hallucination.
        // Local llama produced 33+ fabricated companies during the Vanedge harvest with names
        // that fit the vibe but weren't on the page. Citation check kills that pattern cold.
        var rawCount = jobs.Count;
        var verified = VerifyCitations(jobs, fullContentToAi, out var rejected);
        var citationCount = verified.Count;

        // Legacy heuristic (shared-URL hallucination): still applied AFTER citation check as a
        // safety net for pages where the model copy-pasted plausible-looking text out of nav.
        var hadSharedUrl = verified.Count >= 5 &&
            verified.All(j => string.Equals(j.Url, fetch.FinalUrl, StringComparison.OrdinalIgnoreCase));
        if (hadSharedUrl)
        {
            System.Diagnostics.Debug.WriteLine($"[ai_extraction] rejecting {verified.Count} jobs from {fetch.FinalUrl} — all share source URL (likely hallucination)");
            rejected.AddRange(verified.Select(j => j.Title));
            verified = System.Array.Empty<RawJobPosting>();
        }

        var suspectedHallucination = rejected.Count > 0 || hadSharedUrl;
        var ctx = Logging.RefreshContext.Current;
        var rejectedJson = rejected.Count > 0
            ? JsonSerializer.Serialize(rejected.Take(50).ToList())
            : null;
        _runs.LogAiDecision(ctx?.RunId, ctx?.CompanyId, fetch.FinalUrl, _ai.ProviderId,
                            rawJobsCount: rawCount, acceptedCount: verified.Count,
                            citationVerifiedCount: citationCount,
                            rejectedTitlesJson: rejectedJson,
                            suspectedHallucination: suspectedHallucination);
        jobs = verified;
        if (jobs.Count == 0 && rawCount > 0)
        {
            // We had AI output but rejected every row. Skip the cache write so the next refresh
            // doesn't return a stale empty list — better to retry the AI than freeze a bad day.
            return System.Array.Empty<RawJobPosting>();
        }

        // Only cache when we actually extracted something. An empty result pinned for the 7-day
        // TTL would prevent rediscovery on pages that flip from "no jobs" to "jobs available"
        // mid-week — we'd just keep returning the stale empty list. On a true empty page we'd
        // rather re-pay the AI cost next refresh than miss a new posting.
        if (jobs.Count > 0)
        {
            try
            {
                _cache.Put(fetch.FinalUrl, contentHash, JsonSerializer.Serialize(jobs));
            }
            catch (Exception cacheEx)
            {
                // Cache write must never break the extraction path. The jobs are already parsed.
                System.Diagnostics.Debug.WriteLine($"[ai_extraction_cache] put failed: {cacheEx.Message}");
            }
        }
        return jobs;
    }

    /// <summary>Reject any extracted job whose title doesn't appear in the source content the
    /// model received. <paramref name="rejected"/> collects the discarded titles so they land in
    /// <c>ai_extraction_decisions.rejected_titles</c> for forensic inspection.
    ///
    /// Match is case-insensitive and tolerant of internal whitespace runs (Playwright sometimes
    /// renders titles with non-breaking spaces). We deliberately do NOT require the source_span
    /// to be present — older provider responses won't have it, and we'd reject everything from
    /// the legacy cache. The title-in-source check is the strict gate; source_span is just a
    /// hint the model has to compute (which discourages fabrication).</summary>
    private static IReadOnlyList<RawJobPosting> VerifyCitations(IReadOnlyList<RawJobPosting> jobs,
                                                                 string sourceContent,
                                                                 out List<string> rejected)
    {
        rejected = new List<string>();
        if (jobs.Count == 0) return jobs;
        var normalizedSource = NormalizeForMatch(sourceContent);
        var kept = new List<RawJobPosting>(jobs.Count);
        foreach (var j in jobs)
        {
            var titleNorm = NormalizeForMatch(j.Title);
            if (titleNorm.Length < 3)
            {
                rejected.Add(j.Title);
                continue;
            }
            if (normalizedSource.Contains(titleNorm, StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(j);
            }
            else
            {
                rejected.Add(j.Title);
            }
        }
        return kept;
    }

    private static string NormalizeForMatch(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // Collapse internal whitespace runs and replace non-breaking / weird unicode spaces with
        // ASCII space, so "Senior Engineer" matches "Senior Engineer".
        var t = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        return t;
    }

    private static IReadOnlyList<RawJobPosting> DeserializeCached(string jobsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<List<RawJobPosting>>(jobsJson)
                   ?? new List<RawJobPosting>();
        }
        catch
        {
            // Corrupt cache row — treat as empty so the caller falls back to whatever
            // happens next (in practice the next refresh writes a fresh row).
            return new List<RawJobPosting>();
        }
    }

    private static IReadOnlyList<RawJobPosting> ParseJobs(string responseText, string sourceUrl)
    {
        var json = Jobnet.Services.Ai.JsonExtractor.ExtractJsonObject(responseText);
        var results = new List<RawJobPosting>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("jobs", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return results;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var elt in arr.EnumerateArray())
            {
                if (elt.ValueKind != JsonValueKind.Object) continue;
                var title = StrOrNull(elt, "title");
                if (string.IsNullOrWhiteSpace(title)) continue;

                var url = StrOrNull(elt, "url") ?? sourceUrl;
                var location = StrOrNull(elt, "location");
                var key = $"{title}|{location}|{url}";
                if (!seen.Add(key)) continue;

                results.Add(new RawJobPosting
                {
                    NativeId = ShortHash(key),
                    Title = title!,
                    Url = url,
                    Location = location,
                    RemoteType = StrOrNull(elt, "remote_type"),
                    EmploymentType = StrOrNull(elt, "employment_type"),
                    Department = StrOrNull(elt, "department"),
                });
            }
        }
        catch (Exception ex)
        {
            // The caller treats an empty list as "no jobs found" — same as the model legitimately
            // returning {"jobs":[]} — so we don't rethrow. But the silent swallow used to make
            // parse failures invisible; now we log the offending text so post-mortems are possible.
            Jobnet.Services.Ai.AiLogger.LogParseFailure(
                taskTag: "extraction",
                exception: ex,
                rawResponse: responseText,
                extractedJson: json,
                extraContext: $"sourceUrl={sourceUrl}");
        }
        return results;
    }

    private static string? StrOrNull(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string ShortHash(string s)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).Substring(0, 16).ToLowerInvariant();
    }

    private static readonly Regex AnchorRe = new(
        @"<a[^>]+href=[""'](?<href>[^""']+)[""'][^>]*>(?<text>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TagRe = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WsRe = new(@"\s+", RegexOptions.Compiled);

    private static List<(string Text, string Href)> ExtractAnchors(string html, string baseUrl, int max)
    {
        var list = new List<(string, string)>();
        foreach (Match m in AnchorRe.Matches(html))
        {
            var rawText = TagRe.Replace(m.Groups["text"].Value, " ");
            var text = WsRe.Replace(System.Net.WebUtility.HtmlDecode(rawText), " ").Trim();
            var href = m.Groups["href"].Value.Trim();
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(href)) continue;
            if (href.StartsWith("#") || href.StartsWith("mailto:") || href.StartsWith("tel:")) continue;
            if (text.Length > 120) text = text.Substring(0, 120);
            if (!Uri.IsWellFormedUriString(href, UriKind.Absolute))
            {
                if (Uri.TryCreate(new Uri(baseUrl), href, out var combined))
                    href = combined.ToString();
            }
            list.Add((text, href));
            if (list.Count >= max) break;
        }
        return list;
    }
}
