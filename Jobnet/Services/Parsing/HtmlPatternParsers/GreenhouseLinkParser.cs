using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Jobnet.Services.JobSources;

namespace Jobnet.Services.Parsing.HtmlPatternParsers;

/// <summary>
/// Handles a careers page that just lists direct links to a Greenhouse job board, e.g.:
/// <code>
///   &lt;a href="https://job-boards.greenhouse.io/7shifts/jobs/4740157004"&gt;
///     &lt;h3&gt;Customer Support Representative&lt;/h3&gt;
///     &lt;p&gt;Saskatoon, SK&lt;/p&gt;
///   &lt;/a&gt;
/// </code>
/// Common when a company runs WordPress / Webflow on their own domain but offloads the actual
/// posting pages to Greenhouse. We extract the numeric posting id from the URL (stable across
/// title edits) and pick title / location out of the nested headings.
///
/// Distinct from <c>GreenhouseJobSource</c>: that one calls the boards-api.greenhouse.io JSON
/// endpoint when we know the org's slug; this one reads HTML that surfaces the same jobs
/// directly. Either works — this parser handles the case where ats-detect hasn't (yet)
/// learned the slug for the company.
/// </summary>
public sealed class GreenhouseLinkParser : IHtmlPatternParser
{
    public string Name => "greenhouse_link";

    /// <summary>Matches both <c>boards.greenhouse.io</c> and <c>job-boards.greenhouse.io</c> hostnames
    /// (Greenhouse uses both for embedded boards). Captures org slug and numeric posting id.</summary>
    private static readonly Regex HrefRe = new(
        @"^https?://(?:job-)?boards\.greenhouse\.io/(?<org>[a-zA-Z0-9-]+)/jobs/(?<id>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public bool CanHandle(string url, string html)
    {
        if (string.IsNullOrEmpty(html)) return false;
        // Must contain at least one Greenhouse job link. The full anchor parse happens in Parse;
        // here we just need a fast yes/no.
        return html.Contains("boards.greenhouse.io/", StringComparison.OrdinalIgnoreCase)
            && html.Contains("/jobs/", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<RawJobPosting> Parse(string html, string baseUrl)
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);

        var results = new List<RawJobPosting>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var anchor in doc.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href") ?? "";
            var m = HrefRe.Match(href);
            if (!m.Success) continue;

            var nativeId = m.Groups["id"].Value;
            if (!seen.Add(nativeId)) continue;

            // Title: prefer the first heading inside the anchor, fall back to the anchor's own
            // text content. Some templates wrap the title in <h3>, others use <span>, others
            // put bare text in the anchor.
            var titleEl = anchor.QuerySelector("h1, h2, h3, h4")
                       ?? (IElement?)anchor.QuerySelector("[class*='title']");
            var title = (titleEl?.TextContent ?? anchor.TextContent)?.Trim();
            // The fallback (anchor.TextContent) sometimes includes the location text appended
            // to the title. Strip trailing location-like fragments if both heading + paragraph
            // exist as siblings.
            if (titleEl is not null) title = titleEl.TextContent?.Trim();
            // Collapse internal whitespace so multi-line headings come out as a single line.
            if (!string.IsNullOrWhiteSpace(title))
                title = Regex.Replace(title, @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(title)) continue;

            // Location: heuristic — the next text-bearing sibling of the title, typically <p>.
            string? location = null;
            if (titleEl is not null)
            {
                var sibling = titleEl.NextElementSibling;
                if (sibling is not null && !string.IsNullOrWhiteSpace(sibling.TextContent))
                    location = Regex.Replace(sibling.TextContent.Trim(), @"\s+", " ");
            }

            results.Add(new RawJobPosting
            {
                NativeId = nativeId,
                Title = title!,
                Url = href,
                Location = location,
                RemoteType = null,
                EmploymentType = null,
                Department = null,
            });
        }
        return results;
    }
}
