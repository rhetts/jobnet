using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Jobnet.Services.Ai;
using Jobnet.Services.Playwright;
using Jobnet.Services.Profiling;

namespace Jobnet.Services.Discovery;

public interface ICompanyDirectoryHarvester
{
    Task<HarvestReport> HarvestAsync(string url, string sourceName = "(custom)", string sourceType = "directory",
                                       int maxPages = 1, CancellationToken ct = default);
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
    private readonly IConfigRepository _config;
    private readonly System.Net.Http.HttpClient _http;

    public CompanyDirectoryHarvester(IPlaywrightFetcher fetcher, IAiClient ai,
                                       ICompanyRepository companies,
                                       ICompanyDiscoveryRepository sightings,
                                       IDirectoryCrawlRepository crawls,
                                       IConfigRepository config,
                                       System.Net.Http.HttpClient http)
    {
        _fetcher = fetcher;
        _ai = ai;
        _companies = companies;
        _sightings = sightings;
        _crawls = crawls;
        _config = config;
        _http = http;
    }

    public async Task<HarvestReport> HarvestAsync(string url, string sourceName = "(custom)",
                                                   string sourceType = "directory",
                                                   int maxPages = 1, CancellationToken ct = default)
    {
        var report = new HarvestReport { SourceUrl = url };

        if (!_ai.IsConfigured)
        {
            report.Errors.Add("AI provider not configured.");
            return report;
        }

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
                                                       report, existing, domainsThisRun, ct);

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

        var text = HtmlTextExtractor.Extract(fetch.Html, maxChars: 14_000);
        var anchors = ExtractAnchors(fetch.Html, fetch.FinalUrl, max: 250);
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

        var sourceHost = CanonicalDomain(fetch.FinalUrl);

        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c.Name))
            {
                report.CompaniesSkippedFiltered++;
                continue;
            }
            if (string.IsNullOrWhiteSpace(c.Url))
            {
                report.CompaniesSkippedFiltered++;
                continue;
            }

            var rawDomain = CanonicalDomain(c.Url!);
            string domain;
            string? websiteUrl;

            if (string.IsNullOrEmpty(rawDomain))
            {
                report.CompaniesSkippedFiltered++;
                continue;
            }

            if (string.Equals(rawDomain, sourceHost, StringComparison.OrdinalIgnoreCase))
            {
                var resolved = await TryResolveExternalDomainAsync(c.Url!, sourceHost, ct);
                if (string.IsNullOrEmpty(resolved))
                {
                    report.CompaniesSkippedFiltered++;
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
                continue;
            }

            if (existing.Contains(domain))
            {
                var existingId = _companies.GetByDomain(domain)?.Id ?? 0;
                if (existingId > 0)
                    _sightings.Record(existingId, sourceType, sourceName, fetch.FinalUrl, runId: null);
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
                Notes = $"Harvested from {fetch.FinalUrl}",
                DateDiscovered = DateTime.UtcNow,
            };
            var newId = _companies.Insert(company);
            existing.Add(domain);
            _sightings.Record(newId, sourceType, sourceName, fetch.FinalUrl, runId: null);
            report.CompaniesAdded++;
        }
        return true;
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
    /// profile pages we typically resolve against — they don't need a browser render.</summary>
    private async Task<string?> TryResolveExternalDomainAsync(string profileUrl, string sourceHost, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(profileUrl, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var html = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrEmpty(html)) return null;
            var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? profileUrl;

            var anchors = ExtractAnchors(html, finalUrl, max: 80);
            foreach (var (_, href) in anchors)
            {
                var d = CanonicalDomain(href);
                if (string.IsNullOrEmpty(d)) continue;
                if (d.Equals(sourceHost, StringComparison.OrdinalIgnoreCase)) continue;
                if (IsBlockedDomain(d)) continue;
                if (IsSocialOrUtilityDomain(d)) continue;
                return d;
            }
        }
        catch { /* ignore — return null so caller skips this candidate */ }
        return null;
    }

    private static readonly HashSet<string> SocialUtility = new(StringComparer.OrdinalIgnoreCase)
    {
        "twitter.com", "x.com", "linkedin.com", "facebook.com", "instagram.com",
        "youtube.com", "tiktok.com", "github.com", "medium.com", "substack.com",
        "crunchbase.com", "angel.co", "wellfound.com", "glassdoor.com", "glassdoor.ca",
        "google.com", "maps.google.com", "goo.gl",
    };
    private static bool IsSocialOrUtilityDomain(string domain) => SocialUtility.Contains(domain);

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

    private static string CanonicalDomain(string website)
    {
        try
        {
            var u = website.Contains("://", StringComparison.Ordinal) ? website : $"https://{website}";
            var uri = new Uri(u);
            var host = uri.Host.ToLowerInvariant();
            if (host.StartsWith("www.", StringComparison.Ordinal)) host = host.Substring(4);
            return host;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Block big multinationals and obvious non-companies even if the AI slipped them through.</summary>
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft.com", "google.com", "amazon.com", "apple.com", "meta.com",
        "facebook.com", "oracle.com", "sap.com", "salesforce.com", "adobe.com",
        "ibm.com", "intel.com", "cisco.com", "dell.com", "hp.com",
        "linkedin.com", "indeed.com", "indeed.ca", "glassdoor.com", "glassdoor.ca",
        "twitter.com", "x.com", "instagram.com", "youtube.com", "github.com",
        "builtinvancouver.org", "wearebctech.com", "techstars.com",
        "ycombinator.com", "crunchbase.com", "angellist.com", "wellfound.com",
    };

    private static bool IsBlockedDomain(string domain) => Blocked.Contains(domain);

    private static string? StrOrNull(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static readonly System.Text.RegularExpressions.Regex AnchorRe = new(
        @"<a[^>]+href=[""'](?<href>[^""']+)[""'][^>]*>(?<text>.*?)</a>",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.Singleline
        | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex TagRe =
        new(@"<[^>]+>", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex WsRe =
        new(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static List<(string Text, string Href)> ExtractAnchors(string html, string baseUrl, int max)
    {
        var list = new List<(string, string)>();
        foreach (System.Text.RegularExpressions.Match m in AnchorRe.Matches(html))
        {
            var rawText = TagRe.Replace(m.Groups["text"].Value, " ");
            var text = WsRe.Replace(System.Net.WebUtility.HtmlDecode(rawText), " ").Trim();
            var href = m.Groups["href"].Value.Trim();
            if (string.IsNullOrEmpty(href)) continue;
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

    private sealed class Candidate
    {
        public string? Name { get; set; }
        public string? Url  { get; set; }
        public string? City { get; set; }
    }
}
