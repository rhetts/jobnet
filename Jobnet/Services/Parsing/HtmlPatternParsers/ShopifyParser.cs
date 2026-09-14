using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Jobnet.Services.JobSources;

namespace Jobnet.Services.Parsing.HtmlPatternParsers;

/// <summary>
/// Handles Shopify's own custom-built careers listing at https://www.shopify.com/careers.
/// The page is server-rendered (no JS execution needed — a plain curl fetch returns the full
/// list), grouped into per-team accordions. Each posting is a plain anchor like:
/// <code>
///   &lt;div id="Finance" class="border ... rounded-3xl mb-6 overflow-hidden"&gt;
///     &lt;button aria-controls="accordionBody_Finance"&gt;&lt;span&gt;Finance&lt;/span&gt;...&lt;/button&gt;
///     &lt;div id="accordionBody_Finance"&gt;&lt;div&gt;&lt;div class="p-3"&gt;
///       &lt;a class="group overflow-hidden ... compact-list-layout border-b ..."
///          href="/careers/senior-financial-analyst-multiple-roles_40ef1cb0-5c56-4963-84fe-946708e5d74a"&gt;
///         &lt;h4 class="font-bold text-xl grow"&gt;Senior Financial Analyst - Multiple Roles &lt;/h4&gt;
///         &lt;div class="flex items-center gap-1 location"&gt;
///           &lt;img .../&gt;&lt;span class="text-sm uppercase text-[#1338bf] font-bold"&gt;Remote - Americas&lt;/span&gt;
///         &lt;/div&gt;
///       &lt;/a&gt;
///     &lt;/div&gt;&lt;/div&gt;&lt;/div&gt;
///   &lt;/div&gt;
/// </code>
/// The trailing GUID on the href (e.g. <c>40ef1cb0-5c56-4963-84fe-946708e5d74a</c>) is Shopify's
/// own posting id — it round-trips as the <c>ashby_jid</c> query param on the canonical job URL
/// embedded elsewhere on the page (Shopify's backend is Ashby under the hood, just wrapped in a
/// fully custom front end), confirming it's stable across refreshes rather than something we
/// invented. The page also embeds a Remix/turbo-stream hydration payload with the same job data,
/// but that format is an index-referenced array blob (not plain JSON), far more fragile to parse
/// than the rendered anchors, so we stick to the DOM.
/// </summary>
public sealed class ShopifyParser : IHtmlPatternParser
{
    public string Name => "shopify_careers";

    /// <summary>Matches a job detail link's path: "/careers/&lt;slug&gt;_&lt;guid&gt;". Excludes
    /// non-job careers pages like "/careers" itself or "/careers/extraordinary" (no trailing guid).</summary>
    private static readonly Regex JobHrefRe = new(
        @"^/careers/[a-z0-9-]+_(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public bool CanHandle(string url, string html)
    {
        if (string.IsNullOrEmpty(html)) return false;
        return url.Contains("shopify.com/careers", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<RawJobPosting> Parse(string html, string baseUrl)
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);

        // Per-team accordion bodies are the top-level job-listing containers on this page. If a
        // team currently has zero open roles, Shopify appears to omit its accordion row entirely
        // (observed: category cards advertise "Engineering & Data" etc. but no matching
        // accordion existed for it at fetch time) — so absence of *some* accordions is normal,
        // not a break. We only treat the page as broken when there's no sign of the listings
        // section at all: no accordion bodies AND no "Find your Quest" heading (the static
        // marketing copy that titles this section regardless of how many jobs are open).
        var deptContainers = doc.QuerySelectorAll("[id^='accordionBody_']");
        var hasListingsSection = doc.QuerySelectorAll("h1, h2, h3")
            .Any(h => string.Equals(h.TextContent?.Trim(), "Find your Quest", StringComparison.OrdinalIgnoreCase));

        if (deptContainers.Length == 0 && !hasListingsSection)
        {
            throw new InvalidOperationException(
                "ShopifyParser: expected careers page structure not found (no team accordion " +
                "bodies and no 'Find your Quest' section heading) — page template likely changed.");
        }

        var results = new List<RawJobPosting>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var anchor in doc.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href") ?? "";
            var m = JobHrefRe.Match(href);
            if (!m.Success) continue;

            var nativeId = m.Groups["id"].Value;
            if (!seen.Add(nativeId)) continue;

            // Title lives in the first heading inside the anchor; Shopify uses <h4> for these
            // cards but fall back to other heading levels defensively.
            var titleEl = anchor.QuerySelector("h4, h3, h2");
            var title = titleEl?.TextContent?.Trim();
            if (string.IsNullOrWhiteSpace(title)) continue;
            title = Regex.Replace(title, @"\s+", " ").Trim();

            // Location is the text of the ".location" badge (an icon + a <span>), e.g.
            // "Remote - Americas" or a bare office city like "Toronto".
            string? location = null;
            var locationEl = anchor.QuerySelector(".location");
            if (locationEl is not null && !string.IsNullOrWhiteSpace(locationEl.TextContent))
                location = Regex.Replace(locationEl.TextContent.Trim(), @"\s+", " ");

            // Shopify prefixes remote postings with "Remote - <region>"; every other location
            // observed on the page is a specific office city, i.e. on-site.
            string? remoteType = location is null
                ? null
                : location.StartsWith("Remote", StringComparison.OrdinalIgnoreCase) ? "remote" : "on-site";

            // Team name comes from the enclosing accordion body's id ("accordionBody_<Team>"),
            // which is more reliable than scraping the sibling toggle button's label text.
            var deptContainer = anchor.Closest("[id^='accordionBody_']");
            string? department = deptContainer is not null
                ? deptContainer.Id?["accordionBody_".Length..]
                : null;

            string? absoluteUrl = href;
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
                && Uri.TryCreate(baseUri, href, out var resolved))
            {
                absoluteUrl = resolved.ToString();
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
}
