using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Services.Ai;
using Jobnet.Services.Playwright;
using Jobnet.Services.Profiling;

namespace Jobnet.Services.AtsAdapters;

/// <summary>
/// Fallback job source for companies that don't use a known ATS. Renders the careers page with
/// Playwright, sends the text + visible anchor links to the AI client, and parses a strict-JSON
/// job list out of the response.
/// </summary>
public sealed class AiExtractedJobSource : IAtsJobSource
{
    public string AtsType => "ai_extract";   // companies dispatched here by JobRefresher fallback

    private readonly IPlaywrightFetcher _fetcher;
    private readonly IAiClient _ai;

    public AiExtractedJobSource(IPlaywrightFetcher fetcher, IAiClient ai)
    {
        _fetcher = fetcher;
        _ai = ai;
    }

    /// <summary>Slug here is the URL to fetch (careers_url or homepage).</summary>
    public async Task<IReadOnlyList<RawJobPosting>> FetchAsync(string slug, CancellationToken ct = default)
    {
        if (!_ai.IsConfigured)
            throw new InvalidOperationException("AI client not configured — needed for free-form careers page extraction");

        var fetch = await _fetcher.FetchAsync(slug, ct);
        if (!fetch.Success || string.IsNullOrEmpty(fetch.Html))
            throw new InvalidOperationException(fetch.Error ?? "Playwright fetch failed");

        var text = HtmlTextExtractor.Extract(fetch.Html, maxChars: 10_000);
        var anchors = ExtractAnchors(fetch.Html, fetch.FinalUrl, max: 80);

        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Page rendered but text content is empty");

        var anchorBlock = anchors.Count == 0 ? "(none)" :
            string.Join("\n", anchors.Take(80).Select(a => $"- [{a.Text}] → {a.Href}"));

        var system =
            "You extract ACTUAL job postings from a company's careers page. Output STRICT JSON only — no prose, no markdown.\n" +
            "Schema:\n" +
            "{ \"jobs\": [\n" +
            "  { \"title\": \"...\", \"url\": \"<absolute URL or null>\", \"location\": \"...\", \"remote_type\": \"on-site|hybrid|remote|unknown\", \"employment_type\": \"full-time|part-time|contract|unknown\", \"department\": \"...\" }\n" +
            "] }\n" +
            "\n" +
            "Rules:\n" +
            "- A job title is SPECIFIC: it names a role (e.g. 'Senior Backend Engineer', 'Product Designer'), not a category.\n" +
            "- REJECT department names alone ('Engineering', 'Marketing', 'Sales', 'Product', 'Operations'). These are NOT jobs.\n" +
            "- REJECT navigation items, team pages, locations, or 'view all jobs' links.\n" +
            "- A real job title usually has both a seniority/level word AND a discipline word, or names a specific position.\n" +
            "- Use only the anchor list for URLs — never invent. Skip non-job anchors (privacy, about, blog, etc.).\n" +
            "- If the page only shows department/category filters with no individual postings visible, output {\"jobs\": []}.";

        var user =
            $"Source: {fetch.FinalUrl}\n\n" +
            $"Page text (cleaned):\n{text}\n\n" +
            $"Anchors found on page (label → href):\n{anchorBlock}";

        var response = await _ai.CompleteAsync(user, system, maxTokens: 2048, ct);
        return ParseJobs(response.Text, fetch.FinalUrl);
    }

    private static IReadOnlyList<RawJobPosting> ParseJobs(string responseText, string sourceUrl)
    {
        var json = StripFences(responseText.Trim());
        var results = new List<RawJobPosting>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("jobs", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return results;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var elt in arr.EnumerateArray())
            {
                if (elt.ValueKind != JsonValueKind.Object) continue;
                var title = StrOrNull(elt, "title");
                if (string.IsNullOrWhiteSpace(title)) continue;

                var url = StrOrNull(elt, "url") ?? sourceUrl;
                var location = StrOrNull(elt, "location");
                var key = $"{title}|{location}|{url}";
                if (!seen.Add(key)) continue;

                results.Add(new RawJobPosting
                {
                    NativeId = ShortHash(key),
                    Title = title!,
                    Url = url,
                    Location = location,
                    RemoteType = StrOrNull(elt, "remote_type"),
                    EmploymentType = StrOrNull(elt, "employment_type"),
                    Department = StrOrNull(elt, "department"),
                });
            }
        }
        catch
        {
            // swallow parse error — caller treats empty list as no jobs found
        }
        return results;
    }

    private static string? StrOrNull(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string ShortHash(string s)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).Substring(0, 16).ToLowerInvariant();
    }

    private static string StripFences(string s)
    {
        if (s.StartsWith("```"))
        {
            var firstNl = s.IndexOf('\n');
            if (firstNl > 0) s = s[(firstNl + 1)..];
            if (s.EndsWith("```")) s = s[..^3];
        }
        return s.Trim();
    }

    private static readonly Regex AnchorRe = new(
        @"<a[^>]+href=[""'](?<href>[^""']+)[""'][^>]*>(?<text>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TagRe = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WsRe = new(@"\s+", RegexOptions.Compiled);

    private static List<(string Text, string Href)> ExtractAnchors(string html, string baseUrl, int max)
    {
        var list = new List<(string, string)>();
        foreach (Match m in AnchorRe.Matches(html))
        {
            var rawText = TagRe.Replace(m.Groups["text"].Value, " ");
            var text = WsRe.Replace(System.Net.WebUtility.HtmlDecode(rawText), " ").Trim();
            var href = m.Groups["href"].Value.Trim();
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(href)) continue;
            if (href.StartsWith("#") || href.StartsWith("mailto:") || href.StartsWith("tel:")) continue;
            if (text.Length > 120) text = text.Substring(0, 120);
            if (!Uri.IsWellFormedUriString(href, UriKind.Absolute))
            {
                if (Uri.TryCreate(new Uri(baseUrl), href, out var combined))
                    href = combined.ToString();
            }
            list.Add((text, href));
            if (list.Count >= max) break;
        }
        return list;
    }
}
