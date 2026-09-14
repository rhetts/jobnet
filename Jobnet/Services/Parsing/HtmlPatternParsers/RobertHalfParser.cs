using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Jobnet.Services.JobSources;

namespace Jobnet.Services.Parsing.HtmlPatternParsers;

/// <summary>
/// Handles Robert Half's Canadian jobs search page (https://www.roberthalf.com/ca/en/jobs).
/// The page is served fully populated by AEM/SSR — the first page of results is baked directly
/// into the HTML as custom &lt;rhcl-job-card&gt; elements inside a container that is merely
/// hidden via CSS (a client-side React widget re-renders the same data into #root after load,
/// presumably for pagination/filtering), e.g.:
/// <code>
///   &lt;div id="joblistings"&gt;
///     &lt;div&gt;
///       &lt;rhcl-job-card job-id="05090-0013465600-caen" variant="card"&gt;
///         &lt;a href="https://www.roberthalf.com/ca/en/job/mississauga-on/us-gaap-consultant/05090-0013465600-caen" slot="headline" target="_blank"&gt;
///           US GAAP Consultant
///         &lt;/a&gt;
///         &lt;ul slot="job-info"&gt;
///           &lt;li data-subslot="location"&gt;Mississauga, ON&lt;/li&gt;
///           &lt;li data-subslot="worksite"&gt;remote&lt;/li&gt;
///           &lt;li data-subslot="type"&gt;Temporary&lt;/li&gt;
///           &lt;li data-subslot="salary"&gt;
///             &lt;span data-subslot="salary-min"&gt;60&lt;/span&gt; - &lt;span data-subslot="salary-max"&gt;75&lt;/span&gt; &lt;span data-subslot="salary-currency"&gt;CAD&lt;/span&gt; / &lt;span data-subslot="salary-period"&gt;Hourly&lt;/span&gt;
///           &lt;/li&gt;
///           &lt;li data-subslot="copy"&gt;&amp;lt;p&amp;gt;&amp;lt;strong&amp;gt;US GAAP Conversion Consultant...&amp;lt;/strong&amp;gt;&amp;lt;/p&amp;gt;...&lt;/li&gt;
///           &lt;li data-subslot="date"&gt;2026-09-11T00:00:00Z&lt;/li&gt;
///         &lt;/ul&gt;
///       &lt;/rhcl-job-card&gt;
///       ...
///     &lt;/div&gt;
///   &lt;/div&gt;
/// </code>
/// No JS execution is needed — a plain HTTP fetch returns this markup already populated (verified
/// by curl with a desktop User-Agent). The "copy" li's text is HTML-escaped twice (entities like
/// <c>&amp;lt;p&amp;gt;</c> and <c>&amp;amp;#39;</c>), so extracting a clean description snippet needs an
/// extra tag-strip + a second HTML-decode pass after AngleSharp's normal entity decoding.
/// </summary>
public sealed class RobertHalfParser : IHtmlPatternParser
{
    public string Name => "roberthalf";

    private static readonly Regex TagRe = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRe = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Cheap host check — this parser only ever runs against roberthalf.com pages.</summary>
    public bool CanHandle(string url, string html)
    {
        if (string.IsNullOrEmpty(url)) return false;
        return url.Contains("roberthalf.com", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<RawJobPosting> Parse(string html, string baseUrl)
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);

        // #joblistings is the SSR container that always renders (even with zero results) — its
        // total absence means the page template changed underneath us and needs attention.
        var container = doc.QuerySelector("#joblistings");
        if (container is null)
            throw new InvalidOperationException(
                "Robert Half jobs page: expected container '#joblistings' was not found — page structure may have changed.");

        var cards = container.QuerySelectorAll("rhcl-job-card");
        var results = new List<RawJobPosting>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri);

        foreach (var card in cards)
        {
            // job-id is Robert Half's own stable posting identifier (also the trailing path
            // segment of the job's detail URL), so it survives title/location edits.
            var nativeId = card.GetAttribute("job-id")?.Trim();
            if (string.IsNullOrWhiteSpace(nativeId) || !seen.Add(nativeId)) continue;

            var titleAnchor = card.QuerySelector("a[slot='headline']");
            var title = WhitespaceRe.Replace(titleAnchor?.TextContent?.Trim() ?? "", " ").Trim();
            if (string.IsNullOrWhiteSpace(title)) continue;

            string? url = titleAnchor?.GetAttribute("href");
            if (!string.IsNullOrWhiteSpace(url) && baseUri is not null
                && Uri.TryCreate(baseUri, url, out var absolute))
            {
                url = absolute.ToString();
            }

            var location = Text(card, "li[data-subslot='location']");
            var worksite = Text(card, "li[data-subslot='worksite']");
            var employmentType = Text(card, "li[data-subslot='type']");

            var remoteType = worksite?.ToLowerInvariant() switch
            {
                "remote" => "remote",
                "onsite" => "on-site",
                "hybrid" => "hybrid",
                null or "" => null,
                _ => "unknown",
            };

            var salaryMin = ParseSalary(card, "salary-min");
            var salaryMax = ParseSalary(card, "salary-max");
            var salaryCurrency = Text(card, "span[data-subslot='salary-currency']");
            var salaryPeriodRaw = Text(card, "span[data-subslot='salary-period']");
            var salaryPeriod = salaryPeriodRaw?.ToLowerInvariant() switch
            {
                "hourly" => "hour",
                "weekly" => "week",
                "monthly" => "month",
                "yearly" => "year",
                _ => null,
            };

            // "0 - 0" means Robert Half didn't disclose a salary for this posting, not a $0 job.
            bool hasSalary = (salaryMin.HasValue && salaryMin.Value != 0) || (salaryMax.HasValue && salaryMax.Value != 0);
            string? salaryRange = hasSalary && salaryMin.HasValue && salaryMax.HasValue
                ? $"{salaryMin} - {salaryMax} {salaryCurrency} / {salaryPeriodRaw}".Trim()
                : null;

            var descriptionSnippet = ExtractSnippet(card);

            results.Add(new RawJobPosting
            {
                NativeId = nativeId,
                Title = title,
                Url = url,
                Location = string.IsNullOrWhiteSpace(location) ? null : location,
                RemoteType = remoteType,
                EmploymentType = string.IsNullOrWhiteSpace(employmentType) ? null : employmentType,
                Department = null,
                DescriptionSnippet = descriptionSnippet,
                SalaryRange = salaryRange,
                SalaryMin = hasSalary ? salaryMin : null,
                SalaryMax = hasSalary ? salaryMax : null,
                SalaryCurrency = hasSalary ? salaryCurrency : null,
                SalaryPeriod = hasSalary ? salaryPeriod : null,
            });
        }

        return results;
    }

    private static string? Text(IElement card, string selector)
    {
        var el = card.QuerySelector(selector);
        if (el is null) return null;
        var text = WhitespaceRe.Replace(el.TextContent?.Trim() ?? "", " ").Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static int? ParseSalary(IElement card, string subslot)
    {
        var raw = card.QuerySelector($"span[data-subslot='{subslot}']")?.TextContent?.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Hourly rates come through with several decimal places (e.g. "63.3365") — round to the
        // nearest whole currency unit since RawJobPosting.SalaryMin/Max are ints.
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? (int)Math.Round(value, MidpointRounding.AwayFromZero)
            : null;
    }

    /// <summary>
    /// The "copy" li holds the job description as HTML markup that has been entity-escaped
    /// (sometimes twice, e.g. <c>&amp;amp;#39;</c> for an apostrophe). AngleSharp's TextContent
    /// already reverses the first escaping pass, which leaves literal "&lt;p&gt;...&lt;/p&gt;"
    /// tag text sitting in the string — strip those tags, then run HtmlDecode once more to catch
    /// any double-escaped entities, before collapsing whitespace and truncating.
    /// </summary>
    private static string? ExtractSnippet(IElement card)
    {
        var copyEl = card.QuerySelector("li[data-subslot='copy']");
        var raw = copyEl?.TextContent;
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var noTags = TagRe.Replace(raw, " ");
        var decoded = System.Net.WebUtility.HtmlDecode(noTags);
        var collapsed = WhitespaceRe.Replace(decoded, " ").Trim();
        if (string.IsNullOrWhiteSpace(collapsed)) return null;

        const int maxLength = 500;
        return collapsed.Length > maxLength ? collapsed[..maxLength].TrimEnd() + "..." : collapsed;
    }
}
