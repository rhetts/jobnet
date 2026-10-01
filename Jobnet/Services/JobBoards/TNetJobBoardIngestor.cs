using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Html.Parser;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Jobnet.Services.Classification;
using Jobnet.Services.Discovery;
using Jobnet.Services.Location;
using Jobnet.Services.Playwright;
using Jobnet.Services.Technology;

namespace Jobnet.Services.JobBoards;

/// <summary>
/// Ingests jobs directly from bctechnology.com/jobs/search-results.cfm — T-Net's full BC
/// tech job-search results feed (the same site/markup as
/// <see cref="Discovery.DirectoryPatternParsers.TNetBcTop100Parser"/>, but unfiltered: every
/// posting from every listed employer, not just the Top 100). That parser only ever kept the
/// company name + profile URL (it feeds company *discovery*); this ingests the actual posting.
///
/// Each result row names an employer we may or may not already track. When we don't, we resolve
/// their real external domain the same way <see cref="CompanyDirectoryHarvester"/> does for any
/// other same-host directory profile link (via the shared <see cref="DomainResolution"/> anchor-
/// extraction helper) and create a minimal Company row — if that resolution fails, the row is
/// skipped rather than inventing a domain.
///
/// Fetched via Playwright, not plain HttpClient, despite the page being fully server-rendered
/// (confirmed via curl — no JS needed for content). The site sits behind Cloudflare, which 403s
/// .NET's HttpClient/SslStream regardless of headers (curl and a real browser both pass; this
/// looks like TLS/JA3 fingerprinting, not a header check) — a real browser engine is the only
/// thing that reliably gets through.
/// </summary>
public sealed class TNetJobBoardIngestor : IJobBoardSource
{
    public string Name => "tnet_bc_jobboard";

    private const string SearchUrl = "https://www.bctechnology.com/jobs/search-results.cfm?perpage=50";
    private const string SourceHost = "bctechnology.com";
    private const int MaxPages = 25;

    /// <summary>How long a "profile has no website link" result is trusted before the profile is
    /// re-checked (the employer may add one). Resolved domains are reused indefinitely.</summary>
    private static readonly TimeSpan UnmatchedRetryAfter = TimeSpan.FromDays(14);

    private readonly IPlaywrightFetcher _fetcher;
    private readonly ICompanyRepository _companies;
    private readonly IJobRepository _jobs;
    private readonly HeuristicClassifier _classifier;
    private readonly ITechnologyMatcher _techMatcher;
    private readonly ITechnologyRepository _techs;
    private readonly Filters.FilterRuleProvider _filters;
    private readonly IJobBoardCompanyMapRepository _companyMap;

    public TNetJobBoardIngestor(IPlaywrightFetcher fetcher, ICompanyRepository companies, IJobRepository jobs,
                                 HeuristicClassifier classifier, ITechnologyMatcher techMatcher,
                                 ITechnologyRepository techs, Filters.FilterRuleProvider filters,
                                 IJobBoardCompanyMapRepository companyMap)
    {
        _companyMap = companyMap;
        _fetcher = fetcher;
        _companies = companies;
        _jobs = jobs;
        _classifier = classifier;
        _techMatcher = techMatcher;
        _techs = techs;
        _filters = filters;
    }

    /// <summary>Matches the numeric posting id out of a T-Net job URL, e.g.
    /// <c>/jobs/Sanctuary-AI/157013/Director-Product-Management.cfm</c> -> "157013".</summary>
    private static readonly Regex JobIdRe = new(
        @"^/jobs/[^/]+/(?<id>\d+)/", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<JobBoardIngestResult> IngestAsync(CancellationToken ct = default)
    {
        var result = new JobBoardIngestResult();

        // name -> company id, seeded from what we already know so repeats within/across pages
        // resolve instantly and newly-created companies aren't re-created on a later page.
        var byName = _companies.GetAll()
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        var seenNativeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Employers whose profile fetch failed this run (Cloudflare hiccup, timeout). Not cached
        // in the DB since it says nothing about the employer, but don't retry it for every one
        // of their postings within the same run.
        var failedLookupsThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var page = 1; page <= MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var url = $"{SearchUrl}&page={page}";

            PlaywrightFetchResult fetch;
            try
            {
                fetch = await _fetcher.FetchAsync(url, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Errors.Add($"[page {page}] {ex.GetType().Name}: {ex.Message}");
                break;
            }
            if (!fetch.Success || string.IsNullOrEmpty(fetch.Html))
            {
                result.Errors.Add($"[page {page}] fetch failed: {fetch.Error ?? $"HTTP {fetch.HttpStatus}"}");
                break;
            }

            var parser = new HtmlParser();
            var doc = parser.ParseDocument(fetch.Html);
            var rows = doc.QuerySelectorAll("table[id='table-job-results-row']");

            if (page == 1 && rows.Length == 0)
            {
                result.Errors.Add("Page 1 returned zero result rows — page template may have changed.");
                break;
            }
            if (rows.Length == 0) break; // past the last page

            result.PagesProcessed = page;
            var newIdsOnPage = 0;

            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                result.RowsExamined++;

                var anchor = row.QuerySelector("a#job-title-link");
                var href = anchor?.GetAttribute("href");
                if (string.IsNullOrWhiteSpace(href)) continue;

                var idMatch = JobIdRe.Match(new Uri(new Uri(SearchUrl), href).AbsolutePath);
                if (!idMatch.Success) continue; // not a real posting link — skip, don't guess
                var nativeId = idMatch.Groups["id"].Value;
                if (!seenNativeIds.Add(nativeId)) continue; // same posting seen on an earlier page
                newIdsOnPage++;

                var title = CleanTitle(anchor!.TextContent);
                if (string.IsNullOrWhiteSpace(title)) continue;

                var companyName = row.QuerySelector("span.medium")?.TextContent?.Trim();
                if (string.IsNullOrWhiteSpace(companyName)) continue;

                var cityAnchor = row.QuerySelector("#job-city-sector a");
                var location = cityAnchor?.TextContent?.Trim();

                // Vancouver-area gate before spending a domain-resolution round trip on a company
                // we wouldn't keep the job for anyway.
                if (!LocationMatcher.IsVancouverArea(location))
                {
                    result.SkippedOutOfArea++;
                    continue;
                }

                var jobUrl = new Uri(new Uri(SearchUrl), href).GetLeftPart(UriPartial.Path);

                if (!byName.TryGetValue(companyName, out var companyId))
                {
                    var domain = await ResolveCompanyDomainAsync(companyName, cityAnchor, failedLookupsThisRun, ct);
                    if (domain is null)
                    {
                        result.SkippedUnmatchedCompany++;
                        continue;
                    }

                    var existingByDomain = _companies.GetByDomain(domain);
                    if (existingByDomain is not null)
                    {
                        companyId = existingByDomain.Id;
                    }
                    else
                    {
                        companyId = _companies.Insert(new Company
                        {
                            Id = 0,
                            Name = companyName,
                            Domain = domain,
                            WebsiteUrl = $"https://{domain}",
                            City = string.IsNullOrWhiteSpace(location) ? null : location,
                            Notes = "Discovered via T-Net BC Technology job board",
                            DateDiscovered = DateTime.UtcNow,
                        });
                        result.CompaniesCreated++;
                    }
                    byName[companyName] = companyId;
                }

                var classified = _classifier.Classify(title);
                var job = new Job
                {
                    Id = 0,
                    CompanyId = companyId,
                    Title = title,
                    Url = jobUrl,
                    Location = location,
                    RemoteType = location?.Contains("remote", StringComparison.OrdinalIgnoreCase) == true ? "remote" : null,
                    EmploymentType = null,
                    LevelId = classified.LevelId,
                    AreaIds = classified.Areas.Select(a => a.Id).ToList(),
                    DescriptionSnippet = null,
                    InterestLevel = InterestLevel.Neutral,
                    DateFirstSeen = DateTime.UtcNow,
                    DateLastSeen = DateTime.UtcNow,
                    IsActive = true,
                    SourceStage = "jobboard_tnet",
                };

                var (id, wasNew) = _jobs.Upsert(job, hashKey: $"jobboard_tnet:{companyId}:{nativeId}", hashTier: 1);
                if (wasNew) result.JobsAdded++; else result.JobsUpdated++;

                var techIds = _techMatcher.Match(title);
                _techs.SetForJob(id, techIds);
            }

            // Nothing new on this page: the site served rows we've already seen (it repeats the
            // same results for every page= value past the real end). Every earlier run walked all
            // 25 pages this way, ~30s of rate-limited Playwright each, for the same ~50 postings.
            if (newIdsOnPage == 0) break;

            // Page came back short of a full page of rows — almost certainly the last page.
            if (rows.Length < 40) break;
        }

        return result;
    }

    /// <summary>Same rule <see cref="DomainResolution.TryResolveExternalDomainAsync"/> applies
    /// (first non-source-host, non-blocked outbound anchor on the profile page), but via
    /// Playwright — same Cloudflare wall as the search page itself.</summary>
    /// <summary>Returns <c>Fetched=false</c> when the profile page couldn't be loaded (transient),
    /// vs <c>Fetched=true, Domain=null</c> when it loaded but has no usable outbound link — only
    /// the latter is worth caching.</summary>
    private async Task<(bool Fetched, string? Domain)> TryResolveExternalDomainAsync(string profileUrl, CancellationToken ct)
    {
        try
        {
            var fetch = await _fetcher.FetchAsync(profileUrl, ct);
            if (!fetch.Success || string.IsNullOrEmpty(fetch.Html)) return (false, null);

            var anchors = DomainResolution.ExtractAnchors(fetch.Html, fetch.FinalUrl, max: 80);
            foreach (var (_, href) in anchors)
            {
                var d = DomainResolution.CanonicalDomain(href);
                if (string.IsNullOrEmpty(d)) continue;
                if (d.Equals(SourceHost, StringComparison.OrdinalIgnoreCase)) continue;
                if (_filters.Current.IsHostBlocked(d, Models.FilterScope.Discovery)) continue;
                return (true, d);
            }
            return (true, null);
        }
        catch (Exception) when (ct.IsCancellationRequested == false) { /* ignore — skip this row */ }
        return (false, null);
    }

    /// <summary>Employer name -> website domain, via the <c>jobboard_company_map</c> cache first and
    /// the employer's T-Net profile page only on a miss (or once a cached "no website" result is
    /// older than <see cref="UnmatchedRetryAfter"/>). Null means skip the row.</summary>
    private async Task<string?> ResolveCompanyDomainAsync(string companyName, AngleSharp.Dom.IElement? cityAnchor,
                                                          HashSet<string> failedLookupsThisRun, CancellationToken ct)
    {
        var cached = _companyMap.Get(Name, companyName);
        if (cached is not null
            && (cached.Domain is not null || DateTime.UtcNow - cached.ResolvedAtUtc < UnmatchedRetryAfter))
            return cached.Domain;

        if (failedLookupsThisRun.Contains(companyName)) return null;

        var profileUrl = cityAnchor is null ? null
            : new Uri(new Uri(SearchUrl), cityAnchor.GetAttribute("href") ?? "").ToString();
        if (profileUrl is null) return null;

        var (fetched, domain) = await TryResolveExternalDomainAsync(profileUrl, ct);
        if (!fetched)
        {
            failedLookupsThisRun.Add(companyName);
            return null;
        }
        _companyMap.Save(Name, companyName, domain);
        return domain;
    }

    private static string CleanTitle(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = Regex.Replace(text.Trim(), @"\s+", " ").Trim();
        const string featuredPrefix = "Featured Job: ";
        if (t.StartsWith(featuredPrefix, StringComparison.OrdinalIgnoreCase))
            t = t[featuredPrefix.Length..].Trim();
        return t;
    }
}
