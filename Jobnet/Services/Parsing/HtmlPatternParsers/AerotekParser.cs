using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Jobnet.Services.JobSources;

namespace Jobnet.Services.Parsing.HtmlPatternParsers;

/// <summary>
/// Handles Aerotek's Canadian job search page. https://www.aerotek.com/en-ca/job-search 301s to
/// www.aerotek.com/en/career-opportunities — a marketing/SEO landing page with no job data at all
/// (just a schema.org WebPage block whose SearchAction points at the real listings engine on a
/// different apex domain, jobs.aerotek.com). The actual listings live on that PhenomPeople-hosted
/// site, e.g. jobs.aerotek.com/us/en/search-results or jobs.aerotek.com/ca/fr/search-results
/// (locale routing appears to be geo/IP based — "/ca/en" itself 303s elsewhere — but the field
/// structure below is identical across locales, only the description-teaser language differs).
///
/// The visible job cards on that page are rendered entirely client-side by a Vue3 widget after
/// page load — a plain fetch shows only an empty "no-jobs-area" placeholder in the DOM. But the
/// page still embeds the server-computed initial result set as a big inline JS object assignment
/// so the widget can hydrate without an extra round trip, confirmed via plain curl (no JS
/// execution) against both locales above:
/// <code>
///   &lt;script type="text/javascript"&gt;/*&lt;!--*/ var phApp = phApp || {...};
///     phApp.ddo = {"siteConfig":{...}, ...,
///       "eagerLoadRefineSearch":{"status":"success","data":{ ..., "jobs":[
///         {"jobId":"JP-006282508","title":"Civil Skilled Laborer","city":"Burnaby",
///          "state":"British Columbia","country":"Canada",
///          "cityStateCountry":"Burnaby, British Columbia, Canada","category":"Construction",
///          "type":"Full-time","postedDate":"2026-09-12T17:29:13.703+0000",
///          "descriptionTeaser":"We are expanding our team: As a Civil Skilled Labourer...",
///          "applyUrl":"https://apply.aerotek.com/v1/s/?opco=AERO&amp;params=...&amp;s_id=901&amp;jdg=true"},
///         ...
///       ]}}, ...};/*--&gt;*/&lt;/script&gt;
/// </code>
/// That object literal is valid JSON in every capture we've taken, so no JS engine is needed —
/// just a brace-balanced extraction starting at the first '{' after the "phApp.ddo" marker,
/// handed to System.Text.Json. There is no server-rendered job-detail-page URL anywhere in this
/// blob, only the per-job "applyUrl" apply-flow redirect link, so that's what's surfaced as
/// <see cref="RawJobPosting.Url"/>.
///
/// CanHandle requires the "eagerLoadRefineSearch" marker (not just "phApp.ddo", which appears on
/// other PhenomPeople-hosted pages too) so this parser only fires on pages that actually carry a
/// hydrated jobs array — it naturally no-ops on the www.aerotek.com marketing page and on any
/// jobs.aerotek.com page that isn't the search-results view.
/// </summary>
public sealed class AerotekParser : IHtmlPatternParser
{
    public string Name => "aerotek_phenom_ddo";

    private static readonly Regex WhitespaceRe = new(@"\s+", RegexOptions.Compiled);

    public bool CanHandle(string url, string html)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(html)) return false;
        return url.Contains("aerotek.com", StringComparison.OrdinalIgnoreCase)
            && html.Contains("phApp.ddo", StringComparison.Ordinal)
            && html.Contains("eagerLoadRefineSearch", StringComparison.Ordinal);
    }

    public IReadOnlyList<RawJobPosting> Parse(string html, string baseUrl)
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);

        // Find the <script> carrying the phApp.ddo hydration blob. Its total absence — the page
        // template swapped out from under us, or the widget stopped embedding hydration state —
        // is a genuine break the harvester needs to know about.
        var scriptText = doc.QuerySelectorAll("script")
            .Select(s => s.TextContent)
            .FirstOrDefault(t => t is not null && t.Contains("phApp.ddo", StringComparison.Ordinal));
        if (scriptText is null)
        {
            throw new InvalidOperationException(
                "Aerotek search page: expected inline 'phApp.ddo' hydration script was not found. " +
                "Page template may have changed.");
        }

        var ddoJson = ExtractDdoObject(scriptText);
        if (ddoJson is null)
        {
            throw new InvalidOperationException(
                "Aerotek search page: found the 'phApp.ddo' marker but could not extract a " +
                "balanced JSON object after it. Page script format may have changed.");
        }

        using var jsonDoc = JsonDocument.Parse(ddoJson);
        if (!jsonDoc.RootElement.TryGetProperty("eagerLoadRefineSearch", out var eager) ||
            !eager.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("jobs", out var jobs) ||
            jobs.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "Aerotek search page: 'phApp.ddo.eagerLoadRefineSearch.data.jobs' was not found " +
                "or was not an array. Widget hydration state format may have changed.");
        }

        var results = new List<RawJobPosting>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Path present but genuinely zero entries (no openings for the default location right
        // now) is a legitimately empty page, not a break — falls through to an empty result list.
        foreach (var job in jobs.EnumerateArray())
        {
            var nativeId = GetString(job, "jobId");
            if (string.IsNullOrWhiteSpace(nativeId) || !seen.Add(nativeId)) continue;

            var title = Clean(GetString(job, "title"));
            if (string.IsNullOrWhiteSpace(title)) continue;

            var location = Clean(GetString(job, "cityStateCountry")) ?? BuildLocation(job);

            results.Add(new RawJobPosting
            {
                NativeId = nativeId,
                Title = title!,
                Url = GetString(job, "applyUrl"),
                Location = location,
                RemoteType = null,
                EmploymentType = Clean(GetString(job, "type")),
                Department = Clean(GetString(job, "category")),
                DescriptionSnippet = Truncate(Clean(GetString(job, "descriptionTeaser"))),
            });
        }

        return results;
    }

    /// <summary>
    /// Extracts the balanced <c>{ ... }</c> object literal that immediately follows the
    /// "phApp.ddo" marker (i.e. the right-hand side of <c>phApp.ddo = {...};</c>). Walks the
    /// string tracking JSON-string quote state so braces inside quoted values (descriptions,
    /// escaped characters) don't throw off the depth count — a naive first-'{'-to-last-'}' or
    /// regex match would either under- or over-shoot.
    /// </summary>
    private static string? ExtractDdoObject(string scriptText)
    {
        const string marker = "phApp.ddo";
        var markerIdx = scriptText.IndexOf(marker, StringComparison.Ordinal);
        if (markerIdx < 0) return null;

        var i = markerIdx + marker.Length;
        while (i < scriptText.Length && scriptText[i] != '{') i++;
        if (i >= scriptText.Length) return null;

        var start = i;
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (; i < scriptText.Length; i++)
        {
            var c = scriptText[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return scriptText[start..(i + 1)];
            }
        }
        return null; // unbalanced — never found the matching closing brace
    }

    private static string? GetString(JsonElement obj, string propertyName)
    {
        return obj.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    /// <summary>Falls back to building "city, state, country" by hand for the rare case where
    /// the pre-joined "cityStateCountry" field is missing but the individual parts are present.</summary>
    private static string? BuildLocation(JsonElement job)
    {
        var parts = new[] { GetString(job, "city"), GetString(job, "state"), GetString(job, "country") }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        var joined = string.Join(", ", parts);
        return string.IsNullOrWhiteSpace(joined) ? null : joined;
    }

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return WhitespaceRe.Replace(text.Trim(), " ").Trim();
    }

    private static string? Truncate(string? text)
    {
        const int maxLength = 500;
        if (string.IsNullOrEmpty(text)) return text;
        return text.Length > maxLength ? text[..maxLength].TrimEnd() + "..." : text;
    }
}
