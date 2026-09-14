using System;
using System.Collections.Generic;
using AngleSharp.Html.Parser;

namespace Jobnet.Services.Discovery.DirectoryPatternParsers;

/// <summary>
/// Handles bctechnology.com/tnet100/hiring.cfm — T-Net's "Jobs in BC's Top 100 Tech Companies"
/// feed. This is not a static company roster; it's a ColdFusion job-search-results page
/// pre-filtered (via a hidden <c>posted=last129600</c> field, i.e. the last 36 hours) to postings
/// from Top 100 member companies, re-run fresh on every visit. One &lt;tr&gt; per job posting, shape:
/// <code>
///   &lt;table id="table-job-results-row"&gt;
///    ...
///    &lt;span class="medium"&gt;Netskrt Systems Inc.&lt;/span&gt;
///    &lt;span id="job-city-sector"&gt;
///     &lt;a href="/careers/Netskrt-Systems-Inc-jobs-in-Vancouver-BC"&gt;Vancouver&lt;/a&gt; - InfoTech -
///    &lt;/span&gt;
///   &lt;/table&gt;
/// </code>
/// The company's own site is never linked from this page — every href is same-host
/// (bctechnology.com), either the specific job posting or the company's "/careers/&lt;name&gt;-jobs-in-
/// &lt;city&gt;" page. We take the latter as <c>Url</c>: it is company-scoped (not job-posting-scoped)
/// and is exactly the kind of same-host profile link <c>TryResolveExternalDomainAsync</c> can later
/// follow to find the real outbound company site.
///
/// Because it's a recent-postings feed, zero rows is a normal, frequent state (no Top 100 company
/// happened to post a job in the last 36 hours) — that returns an empty list, not an error. The
/// genuine break signal is the results grid's own column-header cell (&lt;td class="td-job-emp-
/// logo gold-gold"&gt;Company&lt;/td&gt;) going missing, which only happens if T-Net changes the page
/// template (columns renamed/restructured, or a completely different layout returned).
///
/// A single company can appear more than once here (multiple postings in the window), so rows are
/// deduped by company name — only the first occurrence's URL/city is kept.
/// </summary>
public sealed class TNetBcTop100Parser : IDirectoryPatternParser
{
    public string Name => "tnet_bc_top100";

    /// <summary>Host+path check only — this targets one specific known URL
    /// (bctechnology.com/tnet100/hiring.cfm), so there's no ambiguity a marker class would help
    /// resolve.</summary>
    public bool CanHandle(string url, string html)
    {
        if (string.IsNullOrEmpty(url)) return false;
        return url.Contains("bctechnology.com/tnet100", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<DirectoryCandidate> Parse(string html, string baseUrl)
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);

        // The results grid's column-header cell. If this is missing entirely, the page template
        // has changed enough that nothing below can be trusted — that's the genuine break the
        // harvester needs to hear about (logged + falls back to AI), as opposed to the header
        // being present but zero job rows following it right now (a legitimately empty
        // recent-postings window).
        if (doc.QuerySelector("td.td-job-emp-logo.gold-gold") is null)
        {
            throw new InvalidOperationException(
                $"tnet_bc_top100: expected results-grid header cell 'td.td-job-emp-logo.gold-gold' " +
                $"(the Company/Title column heading) not found on {baseUrl} — page template may have changed.");
        }

        var results = new List<DirectoryCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Each job posting renders as its own <table id="table-job-results-row"> — the id repeats
        // once per row (legacy ColdFusion template, not a unique DOM id), which QuerySelectorAll
        // handles fine since CSS attribute matching doesn't require id uniqueness.
        foreach (var row in doc.QuerySelectorAll("table[id='table-job-results-row']"))
        {
            var name = row.QuerySelector("span.medium")?.TextContent?.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue; // malformed single row — skip, not a template break

            if (!seen.Add(name)) continue; // same company, another posting in the window — keep first

            var cityAnchor = row.QuerySelector("#job-city-sector a");
            var url = MakeAbsolute(cityAnchor?.GetAttribute("href"), baseUrl)
                ?? MakeAbsolute(row.QuerySelector("a[href]")?.GetAttribute("href"), baseUrl);

            var city = cityAnchor?.TextContent?.Trim();
            if (string.IsNullOrWhiteSpace(city)) city = null;

            results.Add(new DirectoryCandidate
            {
                Name = name,
                Url = url,
                City = city,
            });
        }

        return results;
    }

    private static string? MakeAbsolute(string? href, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        if (Uri.TryCreate(href, UriKind.Absolute, out var abs)) return abs.ToString();
        if (Uri.TryCreate(new Uri(baseUrl), href, out var combined)) return combined.ToString();
        return href;
    }
}
