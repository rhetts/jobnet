using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Jobnet.Services.JobSources;

namespace Jobnet.Services.Parsing.HtmlPatternParsers;

/// <summary>
/// Handles Swim Recruiting's Vancouver IT/Technology jobs page
/// (https://swimrecruiting.com/find-jobs-vancouver/it-technology/). Swim is a staffing agency —
/// like Robert Half/Aerotek/TEKsystems already in this app, postings never name the actual hiring
/// company (confirmed: teaser text says things like "within a complex public-sector environment",
/// never a company name), so jobs are attributed to Swim itself.
///
/// The page is fully server-rendered — confirmed via plain curl, no JS needed. Each posting is a
/// <c>&lt;div class="c-opportunity"&gt;</c> with the useful fields as data attributes right on the
/// container (unusually clean for this kind of page — no fragile CSS-class-only extraction
/// needed):
/// <code>
///   &lt;div class="c-opportunity c-opportunity--expandable" data-opportunity-category="IT / Technology"
///        data-opportunity-type="Contract" data-opportunity-city="Edmonton" data-opportunity-id="14550"&gt;
///     &lt;h3 class="c-opportunity__title"&gt;DevOps Specialist&lt;/h3&gt;
///     &lt;div class="c-opportunity__location"&gt;...&lt;div class="c-meta-icon__text"&gt;Edmonton, Alberta, Canada&lt;/div&gt;&lt;/div&gt;
///     &lt;div class="c-opportunity__teaser"&gt;
///       12-month contract DevOps Specialist role designing...
///       &lt;a href="/find-jobs-vancouver/it-technology/?job_id=14550" class="c-opportunity__read-more"&gt;Read Full Description&lt;/a&gt;
///     &lt;/div&gt;
///   &lt;/div&gt;
/// </code>
/// <c>data-opportunity-id</c> is Swim's own stable posting id. Despite the URL being scoped to
/// "find-jobs-vancouver", postings are NOT all in Vancouver (confirmed: one was Edmonton, Alberta)
/// — the app's existing Vancouver-area location gate (applied to every source, not something this
/// parser needs to do itself) handles that.
/// </summary>
public sealed class SwimRecruitingParser : IHtmlPatternParser
{
    public string Name => "swim_recruiting";

    private static readonly Regex WhitespaceRe = new(@"\s+", RegexOptions.Compiled);

    public bool CanHandle(string url, string html)
    {
        if (string.IsNullOrEmpty(url)) return false;
        return url.Contains("swimrecruiting.com", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<RawJobPosting> Parse(string html, string baseUrl)
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);

        // The listings container. Present even with zero current openings — its absence means
        // the page template changed, not that Swim has nothing posted right now.
        var container = doc.QuerySelector(".l-opportunities__items");
        if (container is null)
            throw new InvalidOperationException(
                "Swim Recruiting jobs page: expected container '.l-opportunities__items' was not found — page structure may have changed.");

        var results = new List<RawJobPosting>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var card in container.QuerySelectorAll(".c-opportunity"))
        {
            var nativeId = card.GetAttribute("data-opportunity-id")?.Trim();
            if (string.IsNullOrWhiteSpace(nativeId) || !seen.Add(nativeId)) continue;

            var title = Clean(card.QuerySelector(".c-opportunity__title")?.TextContent);
            if (string.IsNullOrWhiteSpace(title)) continue;

            var url = $"{baseUrl.TrimEnd('/')}?job_id={nativeId}";

            // Prefer the full "City, Province, Country" text in the location icon block; fall
            // back to the bare city from the data attribute if that's ever missing.
            var location = Clean(card.QuerySelector(".c-opportunity__location .c-meta-icon__text")?.TextContent)
                ?? Clean(card.GetAttribute("data-opportunity-city"));

            var employmentTypeRaw = Clean(card.GetAttribute("data-opportunity-type"));
            var employmentType = employmentTypeRaw?.ToLowerInvariant() switch
            {
                "permanent" or "full-time" => "full-time",
                "contract" or "temporary" => "contract",
                "part-time" => "part-time",
                null => null,
                _ => "unknown",
            };

            var descriptionSnippet = Clean(card.QuerySelector(".c-opportunity__teaser")?.TextContent);
            // The teaser's own "Read Full Description" link text gets swept up by TextContent —
            // strip it back off rather than store it as if it were part of the job description.
            if (descriptionSnippet is not null)
                descriptionSnippet = descriptionSnippet.Replace("Read Full Description", "").Trim();

            results.Add(new RawJobPosting
            {
                NativeId = nativeId,
                Title = title!,
                Url = url,
                Location = location,
                RemoteType = null, // not exposed on this page's cards
                EmploymentType = employmentType,
                Department = null, // "category" is constant on this page (it's the IT/Tech page itself)
                DescriptionSnippet = string.IsNullOrWhiteSpace(descriptionSnippet) ? null : descriptionSnippet,
            });
        }

        return results;
    }

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return WhitespaceRe.Replace(text.Trim(), " ").Trim();
    }
}
