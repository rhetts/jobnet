using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Jobnet.Services.Ai;
using Jobnet.Services.Discovery.DirectoryPatternParsers;
using Jobnet.Services.Playwright;
using Jobnet.Services.Profiling;

namespace Jobnet.Services.Discovery;

public interface ICompanyDirectoryHarvester
{
    /// <summary><paramref name="seedId"/> is the owning <c>discovery_seeds</c> row id, when this
    /// harvest was triggered from a configured seed rather than an ad-hoc CLI URL — pass it so a
    /// custom-parser success/failure gets recorded on that row and shows up on the Parser Report
    /// screen. Null for ad-hoc harvests (e.g. <c>harvest-directory</c> CLI, aggregator boards).</summary>
    Task<HarvestReport> HarvestAsync(string url, string sourceName = "(custom)", string sourceType = "directory",
                                       int maxPages = 1, CancellationToken ct = default, int? seedId = null);
}

public sealed class HarvestReport
{
    public string SourceUrl { get; set; } = "";
    public int PagesHarvested { get; set; }
    public int CandidatesFound { get; set; }
    public int CompaniesAdded { get; set; }
    public int CompaniesSkippedExisting { get; set; }
    public int CompaniesSkippedFiltered { get; set; }
    public int PageTextChars { get; set; }
    public int AnchorsFound { get; set; }
    public string? RawAiResponse { get; set; }
    public List<string> Errors { get; } = new();
}

/// <summary>
/// Pulls a startup-directory or VC-portfolio page (e.g. builtinvancouver.org,
/// wearebctech.com/about/members, a VC's portfolio page), renders with Playwright,
/// then asks the AI to extract product tech companies + domains. Inserts new companies.
/// </summary>
public sealed class CompanyDirectoryHarvester : ICompanyDirectoryHarvester
{
    private readonly IPlaywrightFetcher _fetcher;
    private readonly IAiClient _ai;
    private readonly ICompanyRepository _companies;
    private readonly ICompanyDiscoveryRepository _sightings;
    private readonly IDirectoryCrawlRepository _crawls;
    private readonly IDiscoverySeedRepository _seeds;
    private readonly DirectoryPatternRegistry _directoryParsers;
    private readonly IConfigRepository _config;
    private readonly System.Net.Http.HttpClient _http;
    private readonly Filters.FilterRuleProvider _filters;

    public CompanyDirectoryHarvester(IPlaywrightFetcher fetcher, IAiClient ai,
                                       ICompanyRepository companies,
                                       ICompanyDiscoveryRepository sightings,
                                       IDirectoryCrawlRepository crawls,
                                       IDiscoverySeedRepository seeds,
                                       DirectoryPatternRegistry directoryParsers,
                                       IConfigRepository config,
                                       Filters.FilterRuleProvider filters,
                                       System.Net.Http.HttpClient http)
    {
        _fetcher = fetcher;
        _ai = ai;
        _companies = companies;
        _sightings = sightings;
        _crawls = crawls;
        _seeds = seeds;
        _directoryParsers = directoryParsers;
        _config = config;
        _filters = filters;
        _http = http;
    }

    /// <summary>Replaces the old Blocked + SocialUtility sets. Both were exact-match, so
    /// ca.linkedin.com slipped through; FilterMatchType.Domain handles subdomains.</summary>
    private bool IsBlockedDomain(string domain)
        => _filters.Current.IsHostBlocked(domain, Models.FilterScope.Discovery);

    public async Task<HarvestReport> HarvestAsync(string url, string sourceName = "(custom)",
                                                   string sourceType = "directory",
                                                   int maxPages = 1, CancellationToken ct = default,
                                                   int? seedId = null)
    {
        var report = new HarvestReport { SourceUrl = url };

        // A custom parser can fully serve a page with no AI involved at all, so this check can't
        // gate the whole harvest the way it used to — it only matters once we actually need AI
        // (no custom parser matched, or one matched but threw). Moved into HarvestSinglePageAsync.

        // Shared state across pages so cross-page dedup works.
        var existing = _companies.GetAll().Select(c => c.Domain).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var domainsThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pages = Math.Max(1, maxPages);

        // Skip pages we've crawled recently (configurable via discovery_skip_days_threshold).
        var skipDaysRaw = _config.GetOrDefault("discovery_skip_days_threshold", "0");
        var skipDays = int.TryParse(skipDaysRaw, out var sd) ? Math.Max(0, sd) : 0;
        var skipCutoffUtc = skipDays > 0 ? DateTime.UtcNow.AddDays(-skipDays) : (DateTime?)null;

        for (int page = 1; page <= pages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var pageUrl = page == 1 && maxPages == 1 ? url : WithPage(url, page);

            if (skipCutoffUtc is not null)
            {
                var last = _crawls.GetLastCrawlUtc(pageUrl);
                if (last is not null && last >= skipCutoffUtc)
                {
                    report.Errors.Add($"[{pageUrl}] Skipped (crawled {Math.Round((DateTime.UtcNow - last.Value).TotalHours, 1)}h ago, within {skipDays}d threshold).");
                    continue;
                }
            }

            var beforeAdds  = report.CompaniesAdded;
            var beforeCands = report.CandidatesFound;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var pageOk = await HarvestSinglePageAsync(pageUrl, sourceName, sourceType,
                                                       report, existing, domainsThisRun, seedId, ct);

            sw.Stop();
            var pageCands = report.CandidatesFound - beforeCands;
            var pageAdded = report.CompaniesAdded - beforeAdds;
            _crawls.Record(pageUrl, DateTime.UtcNow, (int)sw.ElapsedMilliseconds, pageCands, pageAdded,
                            success: pageOk, error: pageOk ? null : "fetch or AI failed");

            if (!pageOk) break;

            report.PagesHarvested = page;
            // Stop early if a page produced no candidates at all — almost certainly past the end.
            if (page < pages && pageCands == 0)
            {
                report.Errors.Add($"Stopped at page {page}: page returned 0 candidates.");
                break;
            }
        }

        return report;
    }

    /// <summary>Fetch + extract + process one page. Updates <paramref name="report"/> in place.
    /// Returns false if the fetch itself failed (caller should stop iterating pages).</summary>
    private async Task<bool> HarvestSinglePageAsync(string url, string sourceName, string sourceType,
                                                     HarvestReport report,
                                                     HashSet<string> existing,
                                                     HashSet<string> domainsThisRun,
                                                     int? seedId,
                                                     CancellationToken ct)
    {
        PlaywrightFetchResult fetch;
        try
        {
            fetch = await _fetcher.FetchAsync(url, ct);
        }
        catch (Exception ex)
        {
            report.Errors.Add($"[{url}] Playwright fetch threw: {ex.Message}");
            return false;
        }
        if (!fetch.Success || string.IsNullOrEmpty(fetch.Html))
        {
            report.Errors.Add($"[{url}] Page fetch failed: {fetch.Error ?? "(empty)"}");
            return false;
        }

        // Try a hand-written parser before ever touching AI. A match that succeeds serves the
        // page entirely deterministically, for free. A match that throws is a real break (the
        // site's template changed) — log it clearly, record it on the owning seed so it surfaces
        // on the Parser Report screen, and fall through to AI so the crawl still produces
        // something instead of just failing outright.
        var customParser = _directoryParsers.ResolveFor(url, fetch.Html);
        if (customParser is not null)
        {
            try
            {
                var parsed = customParser.Parse(fetch.Html, fetch.FinalUrl);
                report.CandidatesFound += parsed.Count;
                if (seedId is int okSeedId)
                    _seeds.SetParserResult(okSeedId, customParser.Name, "ok", null, DateTime.UtcNow);

                var sourceHostForParser = DomainResolution.CanonicalDomain(fetch.FinalUrl);
                await ProcessCandidatesAsync(
                    parsed.Select(p => new Candidate { Name = p.Name, Url = p.Url, City = p.City }).ToList(),
                    sourceType, sourceName, fetch.FinalUrl, sourceHostForParser,
                    report, existing, domainsThisRun, ct);
                return true;
            }
            catch (Exception ex)
            {
                DirectoryParserLogger.LogFailure(customParser.Name, sourceName, url, ex);
                report.Errors.Add($"[{url}] Custom parser '{customParser.Name}' failed: {ex.GetType().Name}: {ex.Message} — falling back to AI.");
                if (seedId is int errSeedId)
                    _seeds.SetParserResult(errSeedId, customParser.Name, "error",
                        $"{ex.GetType().Name}: {ex.Message}", DateTime.UtcNow);
                // Fall through to the AI path below.
            }
        }

        var text = HtmlTextExtractor.Extract(fetch.Html, maxChars: 14_000);
        var anchors = DomainResolution.ExtractAnchors(fetch.Html, fetch.FinalUrl, max: 250);
        // Track the maximum we saw across pages for diagnostics.
        if ((text?.Length ?? 0) > report.PageTextChars) report.PageTextChars = text?.Length ?? 0;
        if (anchors.Count > report.AnchorsFound) report.AnchorsFound = anchors.Count;

        if (string.IsNullOrWhiteSpace(text) && anchors.Count == 0)
        {
            report.Errors.Add($"[{url}] Rendered page is empty.");
            return true; // not a hard stop — next page may work
        }

        var anchorBlock = anchors.Count == 0 ? "(none)" :
            string.Join("\n", anchors.Take(250).Select(a => $"- [{a.Text}] → {a.Href}"));

        var system =
            "You extract company names and links from a directory or portfolio page. " +
            "Be GENEROUS — when in doubt, include the entry. Filtering happens later.\n" +
            "Output STRICT JSON only — no prose, no markdown. Schema:\n" +
            "{ \"companies\": [\n" +
            "  { \"name\": \"Company name\", \"url\": \"https://...\", \"city\": \"Vancouver\" }\n" +
            "] }\n" +
            "\n" +
            "Rules:\n" +
            "- The 'url' field can be the company's external website OR a profile URL on the directory itself. Whichever is in the page is fine.\n" +
            "- Extract EVERY company-looking entry — don't try to guess if they're product-tech vs services. Include them all.\n" +
            "- Do NOT invent URLs — use only ones present in the anchors list.\n" +
            "- 'name' is required. 'url' is optional but strongly preferred.\n" +
            "- If you see 20+ companies on the page, return at least 20.\n" +
            "- If you can't find ANY companies in the content, return {\"companies\": []}.";

        var user =
            $"Source URL: {fetch.FinalUrl}\n\n" +
            $"Page text:\n{text}\n\n" +
            $"Anchors (label → href):\n{anchorBlock}";

        AiResponse response;
        try
        {
            response = await _ai.CompleteAsync(user, system, maxTokens: 8192, ct, task: "directory");
        }
        catch (Exception ex)
        {
            report.Errors.Add($"[{url}] AI call failed: {ex.Message}");
            return false; // AI failed — stop iterating, more pages will fail the same way
        }

        var candidates = ParseCandidates(response.Text);
        report.CandidatesFound += candidates.Count;
        report.RawAiResponse = response.Text;

        var sourceHost = DomainResolution.CanonicalDomain(fetch.FinalUrl);
        await ProcessCandidatesAsync(candidates, sourceType, sourceName, fetch.FinalUrl, sourceHost,
            report, existing, domainsThisRun, ct);
        return true;
    }

    /// <summary>Dedup/filter/insert logic shared by both the custom-parser path and the AI path —
    /// same candidates in, same rules applied, regardless of which extractor produced them.</summary>
    private async Task ProcessCandidatesAsync(IReadOnlyList<Candidate> candidates, string sourceType,
                                               string sourceName, string finalUrl, string sourceHost,
                                               HarvestReport report, HashSet<string> existing,
                                               HashSet<string> domainsThisRun, CancellationToken ct)
    {
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c.Name))
            {
                report.CompaniesSkippedFiltered++;
                report.Errors.Add("[filtered] (unnamed candidate) — no name");
                continue;
            }
            if (string.IsNullOrWhiteSpace(c.Url))
            {
                report.CompaniesSkippedFiltered++;
                report.Errors.Add($"[filtered] {c.Name} — no url");
                continue;
            }

            var rawDomain = DomainResolution.CanonicalDomain(c.Url!);
            string domain;
            string? websiteUrl;

            if (string.IsNullOrEmpty(rawDomain))
            {
                report.CompaniesSkippedFiltered++;
                report.Errors.Add($"[filtered] {c.Name} — unparseable url: {c.Url}");
                continue;
            }

            if (string.Equals(rawDomain, sourceHost, StringComparison.OrdinalIgnoreCase))
            {
                var resolved = await TryResolveExternalDomainAsync(c.Url!, sourceHost, ct);
                if (string.IsNullOrEmpty(resolved))
                {
                    report.CompaniesSkippedFiltered++;
                    report.Errors.Add($"[filtered] {c.Name} — profile page {c.Url} on source host, no external link resolved");
                    continue;
                }
                domain = resolved;
                websiteUrl = $"https://{resolved}";
            }
            else
            {
                domain = rawDomain;
                websiteUrl = $"https://{rawDomain}";
            }

            if (IsBlockedDomain(domain))
            {
                report.CompaniesSkippedFiltered++;
                report.Errors.Add($"[filtered] {c.Name} ({domain}) — blocked by filter rule");
                continue;
            }

            if (existing.Contains(domain))
            {
                var existingId = _companies.GetByDomain(domain)?.Id ?? 0;
                if (existingId > 0)
                    _sightings.Record(existingId, sourceType, sourceName, finalUrl, runId: null);
                report.CompaniesSkippedExisting++;
                continue;
            }
            if (!domainsThisRun.Add(domain))
            {
                report.CompaniesSkippedExisting++;
                continue;
            }

            var company = new Company
            {
                Id = 0,
                Name = c.Name.Trim(),
                Domain = domain,
                WebsiteUrl = websiteUrl,
                City = string.IsNullOrWhiteSpace(c.City) ? null : c.City.Trim(),
                Notes = $"Harvested from {finalUrl}",
                DateDiscovered = DateTime.UtcNow,
            };
            var newId = _companies.Insert(company);
            existing.Add(domain);
            _sightings.Record(newId, sourceType, sourceName, finalUrl, runId: null);
            report.CompaniesAdded++;
        }
    }

    /// <summary>Build the URL for page N.
    /// If the URL contains a literal "{page}" placeholder anywhere, substitute the page number
    /// (this lets the user control the param name / position: ?p={page}, /page/{page}/, etc).
    /// Otherwise fall back to appending or replacing ?page=N, preserving other query params and URL fragments.</summary>
    internal static string WithPage(string url, int page)
    {
        if (url.Contains("{page}", StringComparison.Ordinal))
            return url.Replace("{page}", page.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var hashIdx = url.IndexOf('#');
        var anchor  = hashIdx >= 0 ? url.Substring(hashIdx) : "";
        var bare    = hashIdx >= 0 ? url.Substring(0, hashIdx) : url;
        var qIdx    = bare.IndexOf('?');
        if (qIdx < 0) return $"{bare}?page={page}{anchor}";
        var prefix = bare.Substring(0, qIdx);
        var query  = bare.Substring(qIdx + 1);
        var parts = query.Split('&')
            .Where(s => !string.IsNullOrEmpty(s) && !s.StartsWith("page=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        parts.Add($"page={page}");
        return $"{prefix}?{string.Join("&", parts)}{anchor}";
    }

    /// <summary>Fetch a directory profile page via plain HTTP (no JS) and extract the first
    /// plausible outbound company-website anchor. ~10× faster than Playwright for the static
    /// profile pages we typically resolve against — they don't need a browser render. Shared
    /// with <c>TNetJobBoardIngestor</c> via <see cref="DomainResolution"/> so both apply the
    /// exact same resolution rules.</summary>
    private Task<string?> TryResolveExternalDomainAsync(string profileUrl, string sourceHost, CancellationToken ct)
        => DomainResolution.TryResolveExternalDomainAsync(_http, _filters, profileUrl, sourceHost, ct);

    private static List<Candidate> ParseCandidates(string responseText)
    {
        var list = new List<Candidate>();
        var json = Jobnet.Services.Ai.JsonExtractor.ExtractJsonObject(responseText);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("companies", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var elt in arr.EnumerateArray())
            {
                if (elt.ValueKind != JsonValueKind.Object) continue;
                list.Add(new Candidate
                {
                    Name = StrOrNull(elt, "name"),
                    Url  = StrOrNull(elt, "url") ?? StrOrNull(elt, "website"),
                    City = StrOrNull(elt, "city"),
                });
            }
        }
        catch (Exception ex)
        {
            Jobnet.Services.Ai.AiLogger.LogParseFailure(
                taskTag: "directory",
                exception: ex,
                rawResponse: responseText,
                extractedJson: json);
        }
        return list;
    }

    private static string? StrOrNull(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private sealed class Candidate
    {
        public string? Name { get; set; }
        public string? Url  { get; set; }
        public string? City { get; set; }
    }
}
