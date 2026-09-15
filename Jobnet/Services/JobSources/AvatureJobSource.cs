using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Jobnet.Services.ApiUsage;
using Jobnet.Services.RateLimit;

namespace Jobnet.Services.JobSources;

/// <summary>
/// Avature is an enterprise ATS/CRM. Unlike Greenhouse/Lever/Workday it has no uniform public
/// API — each customer's portal is a heavily customized instance of Avature's "TPT" careers-site
/// template, hosted either on a <c>{tenant}.avature.net</c> subdomain or, commonly for large
/// retailers, on the company's own domain via CNAME (e.g. Lululemon's
/// <c>careers.lululemon.com</c>, confirmed via <c>curl</c> — the HTML contains "avature" but the
/// domain is fully custom).
///
/// What's stable across the TPT template (confirmed empirically against Lululemon's and EA's live
/// portals, no JS execution needed — plain HTTP GET returns the full server-rendered result list):
/// <list type="bullet">
/// <item>Search results page: <c>GET {endpointPath}?{filters}&amp;jobRecordsPerPage=N&amp;jobOffset=N</c>
///   — filters and pagination both work as a plain, stateless querystring GET (no cookies/session
///   needed, no POST required despite the on-page form using method="post"). <c>{endpointPath}</c>
///   itself is NOT standard across tenants — Lululemon's is <c>/en_US/careers/SearchCareer</c>, EA's
///   is <c>/en_US/careers/Home</c> — so, unlike an earlier version of this class, the endpoint path
///   is not hardcoded here; it's part of the stored slug (see below).</item>
/// <item>Lululemon silently caps <c>jobRecordsPerPage</c> at 10 regardless of the value requested;
///   EA's actually honours a requested 20. Pagination must loop via <c>jobOffset</c> either way —
///   confirmed on EA's real filtered URL that consecutive offsets return different postings, not
///   a repeat of page 1 (an earlier blind unfiltered crawl attempt looked like it wasn't paginating
///   at all and wrongly concluded EA had zero Vancouver postings — it was hitting the wrong
///   endpoint/params, not a real empty result).</item>
/// <item>Each result renders as <c>&lt;article class="article article--result"&gt;</c> containing:
///   <code>
///   &lt;h3 class="article__header__text__title..."&gt;&lt;a href="https://.../JobDetail/{slug}/{numericId}"&gt;{title}&lt;/a&gt;&lt;/h3&gt;
///   &lt;div class="article__header__text__subtitle"&gt;&lt;span&gt; Country · State/Region · City &lt;/span&gt;&lt;/div&gt;
///   &lt;div class="article__content"&gt;{boilerplate "who we are..." company blurb, NOT per-job}&lt;/div&gt;
///   </code>
///   The trailing numeric segment of the JobDetail href is Avature's own stable posting id.
///   The subtitle location parts are separated by U+00B7 (middle dot). The <c>article__content</c>
///   block is a fixed company-boilerplate teaser identical on every card (confirmed across all 80
///   Lululemon/Canada/SSC results) — not a real per-job description, so it is intentionally
///   dropped rather than stored as <see cref="RawJobPosting.DescriptionSnippet"/>.</item>
/// <item>The results-count legend ("1-10 of 80 results") gives a real total once a filter narrows
///   the set; with no filters Lululemon shows a display-capped "999+ results" (worldwide, mostly
///   retail-floor postings) rather than a true count.</item>
/// </list>
///
/// Because both the endpoint path itself AND the filter query-param names are entirely per-tenant
/// (Lululemon's Country/Business-Unit filters are form fields "1173"/"6744"; EA's Country filter
/// is field "8171", which also needs a companion "8171_format" + "listFilterMode" param to actually
/// apply — ids/params Avature assigned when each customer's form was built), the slug carries the
/// whole host+path+query a human has already worked out for that tenant, following the same
/// "encode-what-varies" convention as <see cref="WorkdayJobSource"/> (host+site) and
/// <see cref="AmazonJobSource"/> (filter key=value pairs) elsewhere in this file group:
/// <code>
/// ats_slug = "{host}{endpointPath}[?{filter-query-string}]"
/// </code>
/// e.g. for Lululemon, restricting to Canada (country field 1173=15150) and Store Support Centre
/// (business unit field 6744=436 — Lululemon's corporate/tech HQ, analogous to how
/// <see cref="AmazonJobSource"/> uses <c>state=British Columbia</c> to avoid pulling in thousands
/// of irrelevant global postings):
/// <code>
/// careers.lululemon.com/en_US/careers/SearchCareer?1173=15150&amp;6744=436
/// </code>
/// That combination returned 80 real, mostly-corporate/tech Vancouver-HQ postings when tested
/// directly (vs. "999+" with no filter). For EA, restricting to Canada (country field 8171, value
/// 10577 — found by using the site's own filter UI and reading the resulting URL, not guessed):
/// <code>
/// jobs.ea.com/en_US/careers/Home?8171=[10577]&amp;8171_format=5683&amp;listFilterMode=1
/// </code>
/// returned 159 real Canada-wide postings (many in Vancouver) vs. 332 worldwide unfiltered.
/// <c>jobRecordsPerPage</c>/<c>jobOffset</c> are appended by this class per page and must not be
/// included in the stored slug.
/// </summary>
public sealed class AvatureJobSource : IJobSource
{
    public const string Provider = "ats_avature";
    public string AtsType => "avature";

    /// <summary>Avature's TPT template silently caps the list at 10 rows per request no matter
    /// what jobRecordsPerPage asks for — confirmed against Lululemon's live portal.</summary>
    private const int PageSize = 10;

    /// <summary>Safety ceiling on pagination, same shape as <see cref="WorkdayJobSource"/> and
    /// <see cref="AmazonJobSource"/> — guards against a misconfigured/unfiltered slug (e.g. one
    /// that resolves to Lululemon's global "999+" unfiltered set) turning one refresh into an
    /// unbounded crawl.</summary>
    private const int MaxPages = 40;

    private static readonly Regex TotalResultsRe = new(
        @"of\s+(?<n>\d+)\s*\+?\s*results", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly IApiUsageTracker _usage;
    private readonly IRateLimiter _rateLimiter;

    public AvatureJobSource(HttpClient http, IApiUsageTracker usage, IRateLimiter rateLimiter)
    {
        _http = http;
        _usage = usage;
        _rateLimiter = rateLimiter;
    }

    public async Task<IReadOnlyList<RawJobPosting>> FetchAsync(string slug, CancellationToken ct = default)
    {
        var (baseUrl, query) = SplitSlug(slug);

        var results = new List<RawJobPosting>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        var declaredTotal = int.MaxValue; // unknown until the first page tells us

        for (var page = 0; page < MaxPages && offset < declaredTotal; page++)
        {
            var sep = string.IsNullOrEmpty(query) ? "?" : "&";
            var url = $"https://{baseUrl}{query}{sep}jobRecordsPerPage={PageSize}&jobOffset={offset}";

            await _rateLimiter.WaitAsync(Provider, ct);
            _usage.RecordCall(Provider);

            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Avature HTTP {(int)resp.StatusCode} for slug '{slug}' (offset {offset})");

            var html = await resp.Content.ReadAsStringAsync(ct);
            var (postings, total) = ParsePage(html, baseUrl);
            if (total.HasValue) declaredTotal = total.Value;

            if (postings.Count == 0) break; // ran off the end (or offset lands past the real total)

            foreach (var p in postings)
            {
                if (seen.Add(p.NativeId)) results.Add(p);
            }

            offset += PageSize;
        }

        return results;
    }

    /// <summary>Splits a stored slug of the form <c>host/endpointPath[?query]</c> into the base
    /// (host+full endpoint path, no query) and the query string (including the leading '?', or
    /// empty). The endpoint path is tenant-specific (Lululemon: <c>/SearchCareer</c>, EA:
    /// <c>/Home</c>) so, unlike an earlier version of this method, nothing is stripped from it —
    /// whatever path the slug names is hit as-is. Pulled out so <see cref="ParsePage"/>/tests can
    /// hand in a slug without a live HTTP call.</summary>
    public static (string BaseUrl, string Query) SplitSlug(string slug)
    {
        var s = slug.Trim();
        var qIdx = s.IndexOf('?');
        var query = qIdx >= 0 ? s[qIdx..] : string.Empty;
        var path = qIdx >= 0 ? s[..qIdx] : s;
        path = path.TrimEnd('/');
        return (path, query);
    }

    /// <summary>Pulled out for unit testing — parses one already-fetched search-results page
    /// (captured HTML fixture) into postings plus the declared total, if the page states one.</summary>
    public static (IReadOnlyList<RawJobPosting> Postings, int? Total) ParsePage(string html, string baseUrl)
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);

        var totalMatch = TotalResultsRe.Match(html);
        int? total = totalMatch.Success && int.TryParse(totalMatch.Groups["n"].Value, out var n) ? n : null;

        var results = new List<RawJobPosting>();
        foreach (var card in doc.QuerySelectorAll("article.article--result"))
        {
            var anchor = card.QuerySelector(".article__header__text__title a[href]");
            var title = Clean(anchor?.TextContent);
            var href = anchor?.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(href)) continue;

            var nativeId = ExtractNativeId(href) ?? href.Split('?')[0];

            var locationSpan = card.QuerySelector(".article__header__text__subtitle span");
            var location = FormatLocation(locationSpan?.TextContent);

            results.Add(new RawJobPosting
            {
                NativeId = nativeId,
                Title = title!,
                Url = href,
                Location = location,
                RemoteType = ResolveRemoteType(location),
                EmploymentType = null,   // not present on the list card; would need a per-job page fetch.
                Department = null,       // ditto — Avature's list card carries no department field.
                DescriptionSnippet = null, // the card's "article__content" is fixed company
                                           // boilerplate identical on every posting, not a real
                                           // per-job description — see class doc.
            });
        }

        return (results, total);
    }

    private static string? ExtractNativeId(string href)
    {
        var path = href.Split('?')[0].TrimEnd('/');
        var lastSlash = path.LastIndexOf('/');
        var last = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
        return last.Length > 0 && last.All(char.IsDigit) ? last : null;
    }

    /// <summary>Avature's subtitle span is "Country · State/Region · City" (U+00B7-separated,
    /// with irregular whitespace around each part). Re-joins with ", " in the same order.</summary>
    private static string? FormatLocation(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split('·')
            .Select(p => Clean(p))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static string ResolveRemoteType(string? location)
    {
        if (!string.IsNullOrEmpty(location) && location.Contains("remote", StringComparison.OrdinalIgnoreCase))
            return "remote";
        return "unknown";
    }

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return Regex.Replace(text.Trim(), @"\s+", " ").Trim();
    }
}
