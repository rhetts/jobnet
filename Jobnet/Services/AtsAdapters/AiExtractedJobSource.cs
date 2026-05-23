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
using Jobnet.Services.Playwright;
using Jobnet.Services.Profiling;

namespace Jobnet.Services.AtsAdapters;

/// <summary>
/// Fallback job source for companies that don't use a known ATS. Renders the careers page with
/// Playwright, sends the text + visible anchor links to the AI client, and parses a strict-JSON
/// job list out of the response. Also persists discovered URLs to the company_urls cache.
/// </summary>
public sealed class AiExtractedJobSource : IAtsJobSource
{
    public string AtsType => "ai_extract";

    private readonly IPlaywrightFetcher _fetcher;
    private readonly IAiClient _ai;
    private readonly ICompanyUrlsRepository _urls;
    private readonly IAiExtractionCacheRepository _cache;
    private readonly IConfigRepository _config;

    public AiExtractedJobSource(IPlaywrightFetcher fetcher, IAiClient ai, ICompanyUrlsRepository urls,
                                  IAiExtractionCacheRepository cache, IConfigRepository config)
    {
        _fetcher = fetcher;
        _ai = ai;
        _urls = urls;
        _cache = cache;
        _config = config;
    }

    /// <summary>Cache hit count for the most recent FetchForCompanyAsync call (across all AI
    /// extractions invoked). Exposed so callers like JobRefresher can surface this in run logs.</summary>
    public int LastCacheHits { get; private set; }

    /// <summary>Fetch with company context. Persists discovered URLs (anchors + ATS API endpoints
    /// observed in the network log) to company_urls so future refreshes can skip rediscovery.</summary>
    public async Task<IReadOnlyList<RawJobPosting>> FetchForCompanyAsync(int companyId, string url, CancellationToken ct = default)
    {
        var fetch = await _fetcher.FetchAsync(url, ct);
        PersistDiscoveredUrls(companyId, fetch);

        if (!fetch.Success || string.IsNullOrEmpty(fetch.Html))
            throw new InvalidOperationException(fetch.Error ?? "Playwright fetch failed");

        var jsonLd = JsonLdJobExtractor.Extract(fetch.Html, fetch.FinalUrl);
        if (jsonLd.Count > 0)
        {
            if (companyId > 0) _urls.MarkYielded(companyId, fetch.FinalUrl);
            return jsonLd;
        }

        if (!_ai.IsConfigured)
            throw new InvalidOperationException("Page has no JSON-LD job data and AI client not configured");

        var jobs = await ExtractViaAiAsync(fetch, ct);
        if (jobs.Count > 0 && companyId > 0) _urls.MarkYielded(companyId, fetch.FinalUrl);
        return jobs;
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
            "  { \"title\": \"...\", \"url\": \"<absolute URL or null>\", \"location\": \"...\", \"remote_type\": \"on-site|hybrid|remote|unknown\", \"employment_type\": \"full-time|part-time|contract|unknown\", \"department\": \"...\" }\n" +
            "] }\n" +
            "\n" +
            "Rules:\n" +
            "- A job title is SPECIFIC: it names a role (e.g. 'Senior Backend Engineer', 'Product Designer'), not a category.\n" +
            "- REJECT department names alone ('Engineering', 'Marketing', 'Sales', 'Product', 'Operations'). These are NOT jobs.\n" +
            "- REJECT navigation items, team pages, locations, or 'view all jobs' links.\n" +
            "- A real job title usually has both a seniority/level word AND a discipline word, or names a specific position.\n" +
            "- Use only the anchor list for URLs — never invent. Skip non-job anchors (privacy, about, blog, etc.).\n" +
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
        var json = StripFences(responseText.Trim());
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
        catch
        {
            // swallow parse error — caller treats empty list as no jobs found
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

    private static string StripFences(string s)
    {
        if (s.StartsWith("```"))
        {
            var firstNl = s.IndexOf('\n');
            if (firstNl > 0) s = s[(firstNl + 1)..];
            if (s.EndsWith("```")) s = s[..^3];
        }
        return s.Trim();
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
