using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Jobnet.Data;
using Jobnet.Data.Repositories;
using Jobnet.Models;

namespace Jobnet.Services.Discovery;

public sealed class DiscoveryService : IDiscoveryService
{
    private readonly ISearchClient _search;
    private readonly ICompanyRepository _companies;
    private readonly IConfigRepository _config;
    private readonly IDbConnectionFactory _connections;
    private readonly ICompanyDiscoveryRepository _sightings;
    private readonly Filters.FilterRuleProvider _filters;
    private readonly System.Net.Http.HttpClient _http;

    /// <summary>search_terms.type for the `site:jobs.ashbyhq.com Vancouver`-style queries (062).</summary>
    public const string AtsSiteTermType = "ats_site";

    /// <summary>company_discoveries.source_type for companies found via an ATS board URL.</summary>
    public const string AtsSiteSource = "ats_site_search";

    public DiscoveryService(ISearchClient cse, ICompanyRepository companies, IConfigRepository config,
                             IDbConnectionFactory connections, ICompanyDiscoveryRepository sightings,
                             Filters.FilterRuleProvider filters, System.Net.Http.HttpClient http)
    {
        _http = http;
        _search = cse;
        _companies = companies;
        _config = config;
        _connections = connections;
        _sightings = sightings;
        _filters = filters;
    }

    public async Task<DiscoveryReport> RunAsync(int maxQueriesPerTerm = 1, CancellationToken ct = default)
    {
        var terms = GetActiveDiscoveryTerms();
        var scanId = StartScanLog();
        // Compiled once for the whole run — see FilterRuleProvider.
        var filters = _filters.Current;

        var queriesIssued = 0;
        var resultsExamined = 0;
        var resultsSkipped = 0;
        var companiesAdded = 0;
        var companiesSkippedExisting = 0;
        var companiesAtsLinked = 0;
        var errors = new List<string>();
        var addedDomains = new List<string>();
        var domainsSeenThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var atsBoardsSeenThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (term, type) in terms)
        {
            // ATS site: terms get one page each — they run first and Brave's soft cap is ~60
            // calls/day, so paginating them would starve the generic terms.
            var pages = type == AtsSiteTermType ? 1 : maxQueriesPerTerm;
            for (var page = 1; page <= pages; page++)
            {
                ct.ThrowIfCancellationRequested();
                List<SearchResult> results;
                try
                {
                    var r = await _search.SearchAsync(term, page, pageSize: 10, ct);
                    queriesIssued++;
                    results = r.ToList();
                }
                catch (InvalidOperationException ex)
                {
                    // Missing configuration — same provider for every term, no point retrying.
                    errors.Add($"Aborted: {ex.Message}");
                    goto AbortRun;
                }
                catch (SearchAuthException ex)
                {
                    // Auth or quota failure — abort the whole run, no point retrying other terms.
                    errors.Add($"Aborted: {ex.Message}");
                    goto AbortRun;
                }
                catch (Exception ex)
                {
                    errors.Add($"[{term} p{page}] {ex.Message}");
                    break; // skip subsequent pages for this term on error
                }

                foreach (var result in results)
                {
                    resultsExamined++;

                    // An ATS board URL already names the ATS and the account — handle it directly
                    // instead of treating jobs.lever.co etc. as the company's domain. Applies to
                    // every term (generic ones surface board URLs too), not just ats_site ones.
                    if (AtsDetection.AtsDetector.TryMatchAtsUrl(result.Url, out var atsType, out var atsSlug, out var boardUrl))
                    {
                        if (!atsBoardsSeenThisRun.Add($"{atsType}:{atsSlug}"))
                            continue; // several postings from one board in the same results

                        var outcome = await HandleAtsHitAsync(term, result.Url, atsType, atsSlug, boardUrl,
                                                              filters, domainsSeenThisRun, ct);
                        switch (outcome.Kind)
                        {
                            case AtsHitKind.Added:
                                companiesAdded++;
                                addedDomains.Add(outcome.Domain!);
                                break;
                            case AtsHitKind.LinkedExisting:
                                companiesAtsLinked++;
                                companiesSkippedExisting++;
                                break;
                            case AtsHitKind.Existing:
                                companiesSkippedExisting++;
                                break;
                            default:
                                resultsSkipped++;
                                break;
                        }
                        if (outcome.Note is not null) errors.Add($"[{term}] {outcome.Note}");
                        continue;
                    }

                    // An ats_site query should only return board URLs; anything else is off-site
                    // noise, not a company homepage.
                    if (type == AtsSiteTermType)
                    {
                        resultsSkipped++;
                        continue;
                    }

                    var extracted = DomainExtractor.Extract(result.Url);
                    if (extracted is null)
                    {
                        resultsSkipped++;
                        continue;
                    }

                    // Blocklist moved out of DomainExtractor into filter_rule (migration 053),
                    // so the same host rules now also apply on the crawl side.
                    if (filters.IsHostBlocked(extracted.HostDomain, Models.FilterScope.Discovery))
                    {
                        resultsSkipped++;
                        continue;
                    }

                    if (!domainsSeenThisRun.Add(extracted.CanonicalDomain))
                        continue; // already processed this domain in this run

                    var existing = _companies.GetByDomain(extracted.CanonicalDomain);
                    if (existing is not null)
                    {
                        // Record a fresh sighting from this search term + URL.
                        _sightings.Record(existing.Id, "brave_search", term, result.Url, runId: null);
                        companiesSkippedExisting++;
                        continue;
                    }

                    // Cross-attribution guard: when the canonical domain has no token in common with
                    // the search term, the URL probably points at a portfolio / catalog / parent page
                    // rather than the actual company's site. Example: searching "Yaletown Partners
                    // portfolio" returned bdc.ca/.../portfolio/yaletown — bdc.ca is BDC, not Yaletown.
                    // Saving a "Yaletown" company at bdc.ca means we'd scrape BDC forever looking for
                    // Yaletown jobs. Cheaper to skip and let the user re-add manually if needed.
                    if (!DomainMatchesSearchTerm(extracted.CanonicalDomain, term))
                    {
                        resultsSkipped++;
                        errors.Add($"[{term}] skipped {result.Url} — domain '{extracted.CanonicalDomain}' has no token overlap with search term (likely portfolio/catalog page)");
                        continue;
                    }

                    var company = new Company
                    {
                        Id = 0,
                        Name = CompanyNameFromTitle(result.Title, extracted.CanonicalDomain),
                        Domain = extracted.CanonicalDomain,
                        WebsiteUrl = $"https://{extracted.HostDomain}",
                        DateDiscovered = DateTime.UtcNow,
                    };
                    var newId = _companies.Insert(company);
                    _sightings.Record(newId, "brave_search", term, result.Url, runId: null);
                    companiesAdded++;
                    addedDomains.Add(extracted.CanonicalDomain);
                }

                if (results.Count == 0) break; // no more pages
            }
        }

        AbortRun:
        FinishScanLog(scanId, queriesIssued, companiesAdded, errors);
        filters.FlushHits();

        return new DiscoveryReport
        {
            QueriesIssued = queriesIssued,
            ResultsExamined = resultsExamined,
            CompaniesAdded = companiesAdded,
            CompaniesSkippedExisting = companiesSkippedExisting,
            CompaniesAtsLinked = companiesAtsLinked,
            ResultsSkippedFiltered = resultsSkipped,
            Errors = errors,
            AddedDomains = addedDomains,
        };
    }

    /// <summary>Active discovery terms, ATS site: terms first (see RunAsync for why).</summary>
    private IReadOnlyList<(string Term, string Type)> GetActiveDiscoveryTerms()
    {
        using var conn = _connections.Open();
        return conn.Query<(string, string)>(@"
            SELECT term, type FROM search_terms
            WHERE type IN ('company_discovery', @ats) AND is_active = 1
            ORDER BY CASE type WHEN @ats THEN 0 ELSE 1 END, id",
            new { ats = AtsSiteTermType }).ToList();
    }

    private enum AtsHitKind { Added, LinkedExisting, Existing, Skipped }

    private sealed record AtsHitOutcome(AtsHitKind Kind, string? Domain = null, string? Note = null);

    /// <summary>Turn one ATS board hit into a company. Order: already tracked on this ATS account
    /// → just a sighting; resolve the real domain from the board page (none → skip, the ATS host
    /// is never the company); domain already tracked without an ATS → attach the ATS to it;
    /// otherwise insert with ats_type/ats_slug set so it never needs detection.</summary>
    private async Task<AtsHitOutcome> HandleAtsHitAsync(string term, string url, string atsType, string atsSlug,
                                                        string boardUrl, Filters.FilterRuleSet filters,
                                                        HashSet<string> domainsSeenThisRun, CancellationToken ct)
    {
        var byAts = _companies.GetByAts(atsType, atsSlug);
        if (byAts is not null)
        {
            _sightings.Record(byAts.Id, AtsSiteSource, term, url, runId: null);
            return new AtsHitOutcome(AtsHitKind.Existing);
        }

        var domain = await AtsBoardDomainResolver.ResolveAsync(_http, filters, atsType, atsSlug, boardUrl, ct);
        if (domain is null)
            return new AtsHitOutcome(AtsHitKind.Skipped,
                Note: $"skipped {atsType}:{atsSlug} ({boardUrl}) — no company domain found on the board page");

        // Keeps the generic path below from re-handling this domain later in the run.
        domainsSeenThisRun.Add(domain);

        var existing = _companies.GetByDomain(domain);
        if (existing is not null)
        {
            _sightings.Record(existing.Id, AtsSiteSource, term, url, runId: null);
            if (!string.IsNullOrEmpty(existing.AtsType))
                return new AtsHitOutcome(AtsHitKind.Existing);

            _companies.SetAtsInfo(existing.Id, atsType, atsSlug, boardUrl);
            return new AtsHitOutcome(AtsHitKind.LinkedExisting,
                Note: $"linked existing {existing.Name} ({domain}) to {atsType}:{atsSlug}");
        }

        var company = new Company
        {
            Id = 0,
            Name = NameFromDomain(domain),
            Domain = domain,
            WebsiteUrl = $"https://{domain}",
            CareersUrl = boardUrl,
            AtsType = atsType,
            AtsSlug = atsSlug,
            DateDiscovered = DateTime.UtcNow,
        };
        var newId = _companies.Insert(company);
        // Insert doesn't persist ats_slug — same follow-up SeedCompaniesCommand does.
        _companies.SetAtsInfo(newId, atsType, atsSlug, boardUrl);
        _sightings.Record(newId, AtsSiteSource, term, url, runId: null);
        return new AtsHitOutcome(AtsHitKind.Added, Domain: domain);
    }

    private long StartScanLog()
    {
        using var conn = _connections.Open();
        return conn.ExecuteScalar<long>(@"
            INSERT INTO scan_log (scan_time, scan_type, scope, status)
            VALUES (@now, 'discovery', 'global', 'running');
            SELECT last_insert_rowid();",
            new { now = DateTime.UtcNow.ToString("o") });
    }

    private void FinishScanLog(long id, int queries, int added, IReadOnlyList<string> errors)
    {
        using var conn = _connections.Open();
        conn.Execute(@"
            UPDATE scan_log SET status = @status, companies_hit = @queries, jobs_added = @added, errors = @errors
            WHERE id = @id",
            new
            {
                id,
                status = errors.Count == 0 ? "completed" : "partial",
                queries,
                added,
                errors = errors.Count == 0 ? null : JsonSerializer.Serialize(errors)
            });
    }

    /// <summary>Best-effort company name. If the search-result title looks like an article/listicle, fall back to a name derived from the domain.</summary>
    private static string CompanyNameFromTitle(string title, string domain)
    {
        // Default to domain-derived name; only use the title if it actually looks like a company name.
        var domainName = NameFromDomain(domain);

        if (string.IsNullOrWhiteSpace(title)) return domainName;

        // Strip everything after the first " | ", " - ", " — ", " · "  (typical "Title | Site Name" patterns).
        var cut = title;
        foreach (var sep in new[] { " | ", " — ", " - ", " · ", " :: " })
        {
            var idx = cut.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 0) cut = cut[..idx];
        }
        cut = cut.Trim();

        if (cut.Length == 0 || cut.Length > 60) return domainName;
        if (LooksLikeArticleTitle(cut)) return domainName;

        return cut;
    }

    private static bool LooksLikeArticleTitle(string s)
    {
        var lower = s.ToLowerInvariant();

        // Numeric-list markers
        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"^\d+\s")) return true;       // "10 Vancouver companies..."
        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"\b\d{2,3}\b")) return true;  // "Top 100", "Best 50"

        // Title is more than 4 words: probably a sentence/tagline, not a company name
        var wordCount = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount > 4) return true;

        // Generic page titles (homepage section names)
        if (lower is "home" or "homepage" or "about" or "about us" or "careers" or "company"
                 or "what we do" or "who we are" or "our services" or "our work") return true;

        // Starts with question/welcome words → generic
        foreach (var prefix in new[] { "welcome ", "what ", "how ", "why ", "where ", "when ", "who " })
            if (lower.StartsWith(prefix, StringComparison.Ordinal)) return true;

        // Article phrasing
        var bad = new[]
        {
            "top ", "best ", "guide to", "list of", "homegrown",
            "startups to watch", "companies in", "companies to ", "studios in", "startups in",
            "company in", "company to ", "company vancouver", "company bc",
            "you should know", "must-know", "fastest growing",
            "powered ", "ai-powered", "ai powered", "we offer", "we provide",
            "in 202", "in 203",  // year references
        };
        foreach (var marker in bad)
            if (lower.Contains(marker, StringComparison.Ordinal)) return true;

        // Vancouver/BC + a generic descriptor noun → SEO tagline, not a brand
        var hasCity = lower.Contains("vancouver", StringComparison.Ordinal)
                      || System.Text.RegularExpressions.Regex.IsMatch(lower, @"\bbc\b")
                      || lower.Contains("british columbia", StringComparison.Ordinal);
        if (hasCity)
        {
            foreach (var noun in new[] { "company", "solutions", "services", "consulting",
                                          "development", "agency", "security", "studio" })
                if (lower.Contains(noun, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>Token-overlap check between a canonical domain and a search term. Returns true
    /// when at least one meaningful token from the search term appears in the domain.
    /// Used to reject portfolio/catalog/aggregator pages — searching "Yaletown Partners portfolio"
    /// shouldn't accept a hit on bdc.ca because BDC is not Yaletown.</summary>
    internal static bool DomainMatchesSearchTerm(string canonicalDomain, string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(canonicalDomain) || string.IsNullOrWhiteSpace(searchTerm))
            return true; // can't judge — let it through rather than over-rejecting

        // Reduce the domain to just the SLD (e.g. "bdc.ca" → "bdc", "mspcorp.ca" → "mspcorp").
        var stem = canonicalDomain.ToLowerInvariant();
        foreach (var prefix in new[] { "www.", "careers.", "jobs.", "hire.", "go." })
            if (stem.StartsWith(prefix)) stem = stem[prefix.Length..];
        var labels = stem.Split('.');
        var sld = labels.Length >= 2 ? labels[^2] : labels[0];
        sld = sld.Replace('-', ' ').Replace('_', ' ');

        // Stoplist: common words that aren't identifying (location, role, parent/catalog tokens).
        var stop = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            "jobs", "job", "careers", "career", "hiring", "recruit", "recruiting",
            "portfolio", "portfolios", "investment", "investments", "investor",
            "fund", "funds", "capital", "ventures", "venture", "vc", "partners", "partnership",
            "client", "clients", "company", "companies", "co", "inc", "ltd", "llc", "corp", "corporation",
            "the", "and", "of", "for", "in", "to",
            "vancouver", "burnaby", "toronto", "montreal", "calgary", "ottawa", "canada", "bc",
            "tech", "technology", "technologies", "startup", "startups",
        };

        var tokens = searchTerm
            .ToLowerInvariant()
            .Split(new[] { ' ', ',', '-', '_', '/', '.', '\'', '"', '(', ')', '+' },
                   System.StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3 && !stop.Contains(t))
            .ToList();

        if (tokens.Count == 0) return true; // nothing identifying to compare — let it through

        // Substring match in either direction handles "lulu" matching "lululemon" etc.
        foreach (var t in tokens)
            if (sld.Contains(t, System.StringComparison.Ordinal) || t.Contains(sld, System.StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>Derive a presentable name from the domain (e.g. "steamclock.com" → "Steamclock", "blackbird-interactive.com" → "Blackbird Interactive").</summary>
    private static string NameFromDomain(string domain)
    {
        var stem = domain;
        // Strip leading subdomain crumbs like "www.", "careers."
        foreach (var prefix in new[] { "www.", "careers.", "jobs.", "hire.", "go." })
            if (stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) stem = stem[prefix.Length..];

        // Take the second-to-last label (e.g. "steamclock" from "steamclock.com" or "mspcorp" from "west.mspcorp.ca").
        var labels = stem.Split('.');
        var sld = labels.Length >= 2 ? labels[^2] : labels[0];

        // Hyphens, underscores → spaces
        var words = sld.Replace('-', ' ').Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            words[i] = w.Length switch
            {
                0 => w,
                1 => w.ToUpperInvariant(),
                _ => char.ToUpperInvariant(w[0]) + w[1..]
            };
        }
        return string.Join(" ", words);
    }
}
