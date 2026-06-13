using System;
using System.Collections.Generic;
using AngleSharp.Html.Parser;
using Jobnet.Services.JobSources;

namespace Jobnet.Services.Parsing.HtmlPatternParsers;

/// <summary>
/// Handles the WordPress <c>[lever]</c> shortcode plugin, which renders job lists as:
/// <code>
///   &lt;ul class="lever"&gt;
///     &lt;li class="lever-job" id="{uuid}"&gt;
///       &lt;h2 class="lever-job-title"&gt;{title}&lt;/h2&gt;
///       &lt;a class="lever-job-apply" href="https://jobs.lever.co/{org}/{uuid}"&gt;Apply&lt;/a&gt;
///     &lt;/li&gt;
///   &lt;/ul&gt;
/// </code>
/// First spotted on Blackbird Interactive (careers-2/). Several other WordPress sites use the
/// same shortcode — one parser, many companies.
///
/// The <c>id</c> on each <c>&lt;li&gt;</c> is Lever's stable posting GUID, so we use it as the
/// NativeId — that gives us deterministic dedup across refreshes even if the title text
/// changes (e.g. "(closed)" appended).
/// </summary>
public sealed class LeverShortcodeParser : IHtmlPatternParser
{
    public string Name => "lever_shortcode";

    public bool CanHandle(string url, string html)
    {
        if (string.IsNullOrEmpty(html)) return false;
        // Two markers must both appear — the wrapping <ul class="lever"> and the per-item
        // class="lever-job". Either alone could match unrelated pages (the word "lever"
        // is generic), but both together is the shortcode's signature.
        return html.Contains("class=\"lever\"", StringComparison.OrdinalIgnoreCase)
            && html.Contains("lever-job", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<RawJobPosting> Parse(string html, string baseUrl)
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);

        var results = new List<RawJobPosting>();
        foreach (var li in doc.QuerySelectorAll("li.lever-job"))
        {
            var nativeId = li.GetAttribute("id");
            if (string.IsNullOrWhiteSpace(nativeId)) continue;

            var titleEl = li.QuerySelector(".lever-job-title");
            var title = titleEl?.TextContent?.Trim();
            if (string.IsNullOrWhiteSpace(title)) continue;

            var anchor = li.QuerySelector("a.lever-job-apply") ?? li.QuerySelector("a");
            var href = anchor?.GetAttribute("href");
            var absoluteUrl = MakeAbsolute(href, baseUrl);

            results.Add(new RawJobPosting
            {
                NativeId = nativeId.Trim(),
                Title = title!,
                Url = absoluteUrl,
                // The shortcode doesn't expose these in the embedded view; the full posting
                // pages on lever.co do. We leave them null and let downstream enrichment fill
                // them in (or the location matcher accept "unknown").
                Location = null,
                RemoteType = null,
                EmploymentType = null,
                Department = null,
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
