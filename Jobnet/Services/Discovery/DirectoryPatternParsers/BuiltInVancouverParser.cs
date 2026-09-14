using System;
using System.Collections.Generic;
using System.Linq;
using AngleSharp.Html.Parser;

namespace Jobnet.Services.Discovery.DirectoryPatternParsers;

/// <summary>
/// Handles builtinvancouver.org/companies — Built In's paginated Vancouver companies directory
/// (same nationally-templated card-grid engine as builtin.com, builtinboston.com, etc.). Shape:
/// <code>
///   &lt;div id="company-list" class="company-list"&gt;
///     &lt;div class="company-card-horizontal ..."&gt;
///       &lt;div class="company-card-grid"&gt;...
///         &lt;a data-track-id="title" href="/company/mastercard"&gt;
///           &lt;h2&gt;Mastercard&lt;/h2&gt;
///         &lt;/a&gt;
///       &lt;/div&gt;
///     &lt;/div&gt;
///     ... (~20 cards per page)
///   &lt;/div&gt;
/// </code>
/// The card's own "office locations" tooltip lists every office nationwide (e.g. Mastercard shows
/// 22), not a single per-card city, so there's no clean per-company city field to scrape — every
/// company on this specific directory is here because it has a Vancouver presence, so we tag
/// <c>City = "Vancouver"</c> for all candidates (mirrors what the AI-extraction path's prompt
/// already assumes for this same directory).
///
/// A handful of loading-skeleton cards (class <c>placeholder-wave</c>) live in a separate,
/// initially-hidden <c>#company-list-skeleton</c> sibling block, not inside <c>#company-list</c>,
/// so they're naturally excluded — the <c>placeholder-wave</c> filter below is just a defensive
/// backstop in case a future markup revision nests them together.
/// </summary>
public sealed class BuiltInVancouverParser : IDirectoryPatternParser
{
    public string Name => "builtin_vancouver";

    /// <summary>Host+path check only — Built In's card classes aren't guaranteed to stay
    /// human-readable (minified/hashed in other Built In network builds), so we don't rely on a
    /// marker class here, just the URL shape the harvester already requests
    /// (builtinvancouver.org/companies?page=N).</summary>
    public bool CanHandle(string url, string html)
    {
        if (string.IsNullOrEmpty(url)) return false;
        return url.Contains("builtinvancouver.org/companies", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<DirectoryCandidate> Parse(string html, string baseUrl)
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);

        // The top-level listing container. If this is missing entirely, the page template has
        // changed enough that nothing below can be trusted — that's the genuine break the
        // harvester needs to hear about (logged + falls back to AI), as opposed to the container
        // existing but simply holding zero cards right now (a legitimately empty page).
        var container = doc.QuerySelector("#company-list")
            ?? throw new InvalidOperationException(
                $"builtin_vancouver: expected container '#company-list' not found on {baseUrl} — page template may have changed.");

        var results = new List<DirectoryCandidate>();
        var cards = container.QuerySelectorAll(".company-card-horizontal")
            .Where(c => !c.ClassList.Contains("placeholder-wave"));

        foreach (var card in cards)
        {
            var titleAnchor = card.QuerySelector("a[data-track-id='title']");
            if (titleAnchor is null) continue; // malformed single card — skip, not a template break

            var name = titleAnchor.QuerySelector("h2")?.TextContent?.Trim()
                ?? titleAnchor.TextContent?.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var href = titleAnchor.GetAttribute("href");
            var url = MakeAbsolute(href, baseUrl);

            results.Add(new DirectoryCandidate
            {
                Name = name,
                Url = url,
                City = "Vancouver",
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
