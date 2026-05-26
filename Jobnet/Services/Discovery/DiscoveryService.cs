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

    public DiscoveryService(ISearchClient cse, ICompanyRepository companies, IConfigRepository config,
                             IDbConnectionFactory connections, ICompanyDiscoveryRepository sightings)
    {
        _search = cse;
        _companies = companies;
        _config = config;
        _connections = connections;
        _sightings = sightings;
    }

    public async Task<DiscoveryReport> RunAsync(int maxQueriesPerTerm = 1, CancellationToken ct = default)
    {
        var terms = GetActiveDiscoveryTerms();
        var scanId = StartScanLog();

        var queriesIssued = 0;
        var resultsExamined = 0;
        var resultsSkipped = 0;
        var companiesAdded = 0;
        var companiesSkippedExisting = 0;
        var errors = new List<string>();
        var addedDomains = new List<string>();
        var domainsSeenThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var term in terms)
        {
            for (var page = 1; page <= maxQueriesPerTerm; page++)
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
                    var extracted = DomainExtractor.Extract(result.Url);
                    if (extracted is null)
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

        return new DiscoveryReport
        {
            QueriesIssued = queriesIssued,
            ResultsExamined = resultsExamined,
            CompaniesAdded = companiesAdded,
            CompaniesSkippedExisting = companiesSkippedExisting,
            ResultsSkippedFiltered = resultsSkipped,
            Errors = errors,
            AddedDomains = addedDomains,
        };
    }

    private IReadOnlyList<string> GetActiveDiscoveryTerms()
    {
        using var conn = _connections.Open();
        return conn.Query<string>(
            "SELECT term FROM search_terms WHERE type = 'company_discovery' AND is_active = 1 ORDER BY id").ToList();
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
