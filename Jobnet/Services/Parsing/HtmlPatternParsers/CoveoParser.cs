using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Jobnet.Services.JobSources;

namespace Jobnet.Services.Parsing.HtmlPatternParsers;

/// <summary>
/// Handles Coveo's own careers page (https://www.coveo.com/en/company/careers), a custom-built
/// Sitecore site — not an embedded third-party ATS widget.
///
/// That page server-renders a "featured jobs" teaser directly in the page HTML (no JS execution
/// needed to see it — confirmed via plain-HTTP fetch):
/// <code>
///   &lt;div class="jobs-section"&gt;
///       &lt;div class="jobs"&gt;
///           &lt;div class="job"&gt;
///               &lt;a href="/en/company/careers/open-positions/administration/business-strategy-value/business-value-consultant/8768431002"&gt;
///                   &lt;span class="job-title"&gt;Business Value Consultant&lt;/span&gt;
///                   &lt;span&gt;Business Strategy &amp; Value&lt;/span&gt;
///                   &lt;span class="separator"&gt;&lt;/span&gt;
///                   &lt;span&gt;Canada; Montreal (Province of Quebec, Canada); Quebec City (Province of Quebec, Canada)&lt;/span&gt;
///               &lt;/a&gt;
///           &lt;/div&gt;
///       &lt;/div&gt;
///   &lt;/div&gt;
/// </code>
/// The trailing numeric segment on the href (8768431002) is Coveo's own Sitecore item id for the
/// posting — stable across title edits — so it's used as NativeId rather than hashing the title.
///
/// Important caveat found while building this: the section immediately above this widget reads
/// "Check out our featured roles or view the full list of opportunities..." — this is a curated
/// sample, not the complete open-roles list. The real full listing lives at
/// /en/company/careers/open-positions, but that page has no server-rendered job data at all: it's
/// a "CoveoForSitecore" search interface (Coveo's own search-as-a-service product, eating its own
/// dog food) whose result cards are built client-side from an underscore.js template
/// (<c>&lt;%= coveoFieldValue('_name') %&gt;</c>) after an AJAX query against their search index —
/// there is no embedded JSON to fall back to on that page. This parser intentionally targets only
/// the overview page's static widget, which is real, stable, zero-JS data; it will under-report
/// vs. the full board but costs zero AI calls and never fabricates data.
/// </summary>
public sealed class CoveoParser : IHtmlPatternParser
{
    public string Name => "coveo_featured_jobs";

    public bool CanHandle(string url, string html)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(html)) return false;
        // Host check plus a cheap marker for the featured-jobs widget's own container class, so
        // we don't misfire on some other coveo.com page that doesn't carry it.
        return url.Contains("coveo.com", StringComparison.OrdinalIgnoreCase)
            && html.Contains("jobs-section", StringComparison.OrdinalIgnoreCase)
            && html.Contains("job-title", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<RawJobPosting> Parse(string html, string baseUrl)
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);

        // The widget's own top-level container. If this is gone entirely, Coveo has changed
        // their careers page layout (removed the widget, renamed the class, etc.) — a genuine
        // break the harvester needs to know about so it can fall back to AI.
        var container = doc.QuerySelector(".jobs-section");
        if (container is null)
        {
            throw new InvalidOperationException(
                "Coveo careers page: expected container '.jobs-section' not found. " +
                "Page layout may have changed.");
        }

        var results = new List<RawJobPosting>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Container present but no per-job nodes (Coveo genuinely has zero featured roles right
        // now) — that's a legitimately empty page, not a break. Return an empty list.
        foreach (var job in container.QuerySelectorAll(".job"))
        {
            var anchor = job.QuerySelector("a[href]");
            if (anchor is null) continue;

            var titleEl = anchor.QuerySelector("span.job-title");
            var title = Clean(titleEl?.TextContent);
            if (string.IsNullOrWhiteSpace(title)) continue;

            var href = anchor.GetAttribute("href");
            var absoluteUrl = MakeAbsolute(href, baseUrl);

            var nativeId = ExtractNativeId(href) ?? absoluteUrl?.Split('?')[0];
            if (string.IsNullOrWhiteSpace(nativeId)) continue; // no id and no url to key off of
            if (!seen.Add(nativeId)) continue;

            // The remaining (non job-title, non separator) <span> children of the anchor carry
            // department then location, in that order — see the markup quoted above.
            var infoSpans = anchor.Children
                .Where(e => e.TagName.Equals("span", StringComparison.OrdinalIgnoreCase)
                    && !e.ClassList.Contains("job-title")
                    && !e.ClassList.Contains("separator"))
                .ToList();

            var department = infoSpans.Count > 0 ? Clean(infoSpans[0].TextContent) : null;
            var location = infoSpans.Count > 1 ? Clean(infoSpans[1].TextContent) : null;

            string? remoteType = null;
            if (!string.IsNullOrEmpty(location) &&
                location.Contains("remote", StringComparison.OrdinalIgnoreCase))
            {
                remoteType = "remote";
            }

            results.Add(new RawJobPosting
            {
                NativeId = nativeId,
                Title = title!,
                Url = absoluteUrl,
                Location = location,
                RemoteType = remoteType,
                EmploymentType = null,
                Department = department,
            });
        }

        return results;
    }

    /// <summary>Extracts Coveo's Sitecore item id — the trailing numeric path segment of the
    /// job's href, e.g. ".../business-value-consultant/8768431002" -> "8768431002". Stable across
    /// title/slug edits since it's assigned once per posting.</summary>
    private static string? ExtractNativeId(string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        var path = href.Split('?')[0].TrimEnd('/');
        var lastSlash = path.LastIndexOf('/');
        var last = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
        return last.Length > 0 && last.All(char.IsDigit) ? last : null;
    }

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return Regex.Replace(text.Trim(), @"\s+", " ").Trim();
    }

    private static string? MakeAbsolute(string? href, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        if (Uri.TryCreate(href, UriKind.Absolute, out var abs)) return abs.ToString();
        if (Uri.TryCreate(new Uri(baseUrl), href, out var combined)) return combined.ToString();
        return href;
    }
}
