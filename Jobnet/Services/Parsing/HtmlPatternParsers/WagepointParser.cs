using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Jobnet.Services.JobSources;

namespace Jobnet.Services.Parsing.HtmlPatternParsers;

/// <summary>
/// Handles Wagepoint's careers page (https://www.wagepoint.com/careers/), which embeds a
/// Teamtailor "jobs widget" rather than linking out to a native ATS we already support:
/// <code>
///   &lt;div id="jobs"&gt;
///     &lt;script src="https://scripts.teamtailor-cdn.com/widgets/eu-pink/jobs.js" async&gt;&lt;/script&gt;
///     &lt;div class="teamtailor-jobs-widget" data-teamtailor-api-key="..." ...&gt;&lt;/div&gt;
///   &lt;/div&gt;
/// </code>
/// The raw page HTML (plain HTTP fetch) ships an *empty* &lt;div class="teamtailor-jobs-widget"&gt;
/// — there is no embedded JSON fallback. The widget script fetches
/// <c>https://api.teamtailor.com/v1/jobs?api_key=...</c> client-side and builds the DOM itself
/// (confirmed by reading the widget's own JS source, fetched directly). Since this app hands
/// parsers HTML from a real Playwright-rendered page, by the time we see it the widget script
/// has already run and populated the container with (per job):
/// <code>
///   &lt;div class="teamtailor-jobs__job"&gt;
///     &lt;a class="teamtailor-jobs__job-title" href="https://wagepoint.teamtailor.com/jobs/8355769-senior-content-marketing-manager?utm_campaign=..."&gt;
///       Senior Content Marketing Manager
///     &lt;/a&gt;
///     &lt;span class="teamtailor-jobs__job-info"&gt;
///       &lt;span class="teamtailor-jobs__department"&gt;Marketing&lt;/span&gt; -
///       &lt;span class="teamtailor-jobs__role"&gt;...&lt;/span&gt; -
///       &lt;span class="teamtailor-jobs__location"&gt;Canada - REMOTE&lt;/span&gt;
///     &lt;/span&gt;
///   &lt;/div&gt;
/// </code>
/// Those per-field class names (job/job-title/job-info/department/role/region/location/company/
/// division/remote_status) are hard-coded in Teamtailor's widget bundle, not specific to this
/// company's theme, so they're a solid, stable selector target. The numeric id embedded in the
/// job-title href (teamtailor.com/jobs/&lt;id&gt;-&lt;slug&gt;) is Teamtailor's own stable posting id —
/// used as NativeId rather than hashing the title, so postings survive title edits.
/// </summary>
public sealed class WagepointParser : IHtmlPatternParser
{
    public string Name => "wagepoint_teamtailor";

    /// <summary>Matches the numeric posting id out of a Teamtailor careersite job URL, e.g.
    /// <c>https://wagepoint.teamtailor.com/jobs/8355769-senior-content-marketing-manager</c>.</summary>
    private static readonly Regex JobIdRe = new(
        @"teamtailor\.com/jobs/(?<id>\d+)-",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public bool CanHandle(string url, string html)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(html)) return false;
        // Host check (per the interface contract) plus a cheap marker so we don't misfire on
        // some other page under wagepoint.com that happens not to carry the widget.
        return url.Contains("wagepoint.com", StringComparison.OrdinalIgnoreCase)
            && html.Contains("teamtailor", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<RawJobPosting> Parse(string html, string baseUrl)
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);

        // The widget's own top-level container. If this is gone entirely, Wagepoint has changed
        // their careers page layout (removed the widget, renamed the class, switched ATS, etc.)
        // — a genuine break the harvester needs to know about so it can fall back to AI.
        var container = doc.QuerySelector(".teamtailor-jobs-widget");
        if (container is null)
        {
            throw new InvalidOperationException(
                "Wagepoint careers page: expected container '.teamtailor-jobs-widget' not found. " +
                "Page layout or ATS may have changed.");
        }

        var results = new List<RawJobPosting>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Container present but no per-job nodes yet (widget script didn't run / hasn't finished,
        // or Wagepoint genuinely has zero open roles right now) — that's a legitimately empty
        // page, not a break. Return an empty list.
        foreach (var job in container.QuerySelectorAll(".teamtailor-jobs__job"))
        {
            var anchor = job.QuerySelector("a.teamtailor-jobs__job-title");
            var title = anchor?.TextContent?.Trim();
            if (string.IsNullOrWhiteSpace(title)) continue;
            title = Regex.Replace(title, @"\s+", " ").Trim();

            var href = anchor?.GetAttribute("href");
            var absoluteUrl = MakeAbsolute(href, baseUrl);

            var idMatch = href is null ? null : JobIdRe.Match(href);
            // Prefer Teamtailor's own numeric posting id (stable across title edits). Fall back
            // to the full job URL with tracking query params stripped if the pattern ever
            // changes shape — still stable across refreshes, just not as tidy.
            string nativeId;
            if (idMatch is { Success: true })
            {
                nativeId = idMatch.Groups["id"].Value;
            }
            else if (!string.IsNullOrWhiteSpace(absoluteUrl))
            {
                nativeId = absoluteUrl.Split('?')[0];
            }
            else
            {
                continue; // no title link and no id to key off of — skip rather than guess.
            }
            if (!seen.Add(nativeId)) continue;

            var department = job.QuerySelector(".teamtailor-jobs__department")?.TextContent?.Trim();
            var role = job.QuerySelector(".teamtailor-jobs__role")?.TextContent?.Trim();
            var region = job.QuerySelector(".teamtailor-jobs__region")?.TextContent?.Trim();
            var location = job.QuerySelector(".teamtailor-jobs__location")?.TextContent?.Trim();

            var combinedLocation = CombineLocation(location, region);

            // The widget only renders a "remote status" span when the page explicitly enables
            // that filter (Wagepoint's embed doesn't). Teamtailor location names for this
            // company do carry it in plain text though, e.g. "Canada - REMOTE".
            string? remoteType = null;
            if (!string.IsNullOrEmpty(combinedLocation) &&
                combinedLocation.Contains("remote", StringComparison.OrdinalIgnoreCase))
            {
                remoteType = "remote";
            }

            results.Add(new RawJobPosting
            {
                NativeId = nativeId,
                Title = title!,
                Url = absoluteUrl,
                Location = combinedLocation,
                RemoteType = remoteType,
                EmploymentType = null,
                Department = department,
            });
        }

        return results;
    }

    private static string? CombineLocation(string? location, string? region)
    {
        if (string.IsNullOrWhiteSpace(location)) return string.IsNullOrWhiteSpace(region) ? null : region;
        if (string.IsNullOrWhiteSpace(region)) return location;
        if (location.Contains(region, StringComparison.OrdinalIgnoreCase)) return location;
        return $"{location}, {region}";
    }

    private static string? MakeAbsolute(string? href, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        if (Uri.TryCreate(href, UriKind.Absolute, out var abs)) return abs.ToString();
        if (Uri.TryCreate(new Uri(baseUrl), href, out var combined)) return combined.ToString();
        return href;
    }
}
