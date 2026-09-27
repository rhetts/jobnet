using System;
using System.Collections.Generic;
using System.Globalization;
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
/// Randstad Canada (staffing agency, randstad.ca) — server-rendered location search pages, no
/// API or JS needed (plain curl gets the full listing):
/// <code>
/// GET https://www.randstad.ca/jobs/{slug}/            page 1
/// GET https://www.randstad.ca/jobs/{slug}/page-{N}/   page N (30 cards each; past the end = 0 cards)
/// </code>
/// Slug = the location path, e.g. <c>british-columbia/vancouver</c> (165 postings on
/// 2026-09-27, covering Burnaby/Richmond/North Van etc. too). Each card is an
/// <c>li.cards__item</c> with the title link (<c>a.cards__link</c>, href
/// <c>/jobs/{title-slug}_{city}_{id}/</c>), a <c>ul.cards__meta</c> list of location / job type
/// ("Contract" | "Permanent") / optional pay ("$50.00 - $70.00 per hour"), and a short
/// <c>.cards__description</c>. The numeric id at the end of the href is the stable posting id.
/// The header "N jobs found" gives the total.
/// </summary>
public sealed class RandstadJobSource : IJobSource
{
    public const string Provider = "ats_randstad";
    public string AtsType => "randstad";

    private const string BaseUrl = "https://www.randstad.ca";
    private const int MaxPages = 40; // 40*30 = 1,200 postings; Vancouver is ~165

    private static readonly Regex JobIdRe = new(@"_(?<id>\d+)/?$", RegexOptions.Compiled);
    private static readonly Regex TotalRe = new(@"(?<n>[\d,]+)\s+jobs?\s+found", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PayRe = new(
        @"\$(?<min>[\d,]+(?:\.\d+)?)(?:\s*-\s*\$(?<max>[\d,]+(?:\.\d+)?))?\s*per\s+(?<period>hour|year|month)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HttpClient _http;
    private readonly IApiUsageTracker _usage;
    private readonly IRateLimiter _rateLimiter;

    public RandstadJobSource(HttpClient http, IApiUsageTracker usage, IRateLimiter rateLimiter)
    {
        _http = http;
        _usage = usage;
        _rateLimiter = rateLimiter;
    }

    public async Task<IReadOnlyList<RawJobPosting>> FetchAsync(string slug, CancellationToken ct = default)
    {
        var path = slug.Trim().Trim('/');
        var results = new List<RawJobPosting>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int? declaredTotal = null;

        for (var page = 1; ; page++)
        {
            // Hitting the ceiling means a truncated list, and the refresher would close every
            // posting past it. Fail the fetch instead so nothing gets closed.
            if (page > MaxPages)
                throw new InvalidOperationException(
                    $"Randstad slug '{slug}' exceeded {MaxPages} pages; result would be truncated");

            ct.ThrowIfCancellationRequested();
            await _rateLimiter.WaitAsync(Provider, ct);
            _usage.RecordCall(Provider);

            var url = page == 1 ? $"{BaseUrl}/jobs/{path}/" : $"{BaseUrl}/jobs/{path}/page-{page}/";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Randstad HTTP {(int)resp.StatusCode} for slug '{slug}' (page {page})",
                                                null, resp.StatusCode);

            var html = await resp.Content.ReadAsStringAsync(ct);
            var (postings, total) = ParsePage(html);
            if (page == 1)
            {
                declaredTotal = total;
                // Page 1 of a real location search always has the "N jobs found" header; without
                // it this is likely a changed template or an error page, not an empty board.
                if (postings.Count == 0 && total is null)
                    throw new InvalidOperationException(
                        $"Randstad page 1 for slug '{slug}' had no job cards and no result count — template may have changed");
            }

            if (postings.Count == 0) break; // past the last page
            var added = 0;
            foreach (var p in postings)
                if (seen.Add(p.NativeId)) { results.Add(p); added++; }
            if (added == 0) break; // site repeating a page — nothing new to find
            if (declaredTotal is int t && results.Count >= t) break;
        }
        return results;
    }

    /// <summary>Pulled out for unit testing — parses one fetched search page into postings plus
    /// the declared total ("165 jobs found"), if present.</summary>
    public static (IReadOnlyList<RawJobPosting> Postings, int? Total) ParsePage(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);

        var totalMatch = TotalRe.Match(html);
        int? total = totalMatch.Success
                     && int.TryParse(totalMatch.Groups["n"].Value.Replace(",", ""), out var n) ? n : null;

        var results = new List<RawJobPosting>();
        foreach (var card in doc.QuerySelectorAll("li.cards__item"))
        {
            var anchor = card.QuerySelector("a.cards__link[href]");
            var title = Clean(anchor?.TextContent);
            var href = anchor?.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(href)) continue;

            var idMatch = JobIdRe.Match(href.Split('?')[0]);
            if (!idMatch.Success) continue; // not a posting link — skip, don't guess an id

            var metas = card.QuerySelectorAll("ul.cards__meta > li.cards__meta-item")
                .Select(li => Clean(li.TextContent))
                .Where(m => !string.IsNullOrEmpty(m))
                .Select(m => m!)
                .ToList();
            var pay = metas.Select(m => PayRe.Match(m)).FirstOrDefault(m => m.Success);
            var jobType = metas.FirstOrDefault(m => m.Equals("Contract", StringComparison.OrdinalIgnoreCase)
                                                 || m.Equals("Permanent", StringComparison.OrdinalIgnoreCase));
            // Location is the first meta item that isn't the job type or the pay line.
            var location = metas.FirstOrDefault(m => m != jobType && !PayRe.IsMatch(m));

            results.Add(new RawJobPosting
            {
                NativeId = idMatch.Groups["id"].Value,
                Title = title!,
                Url = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : BaseUrl + href,
                Location = location,
                RemoteType = location?.Contains("remote", StringComparison.OrdinalIgnoreCase) == true ? "remote" : "unknown",
                EmploymentType = jobType?.ToLowerInvariant() switch
                {
                    "contract" => "contract",
                    "permanent" => "full-time",
                    _ => null,
                },
                Department = null,
                DescriptionSnippet = SnippetCleaner.Clean(card.QuerySelector(".cards__description")?.TextContent, maxChars: 500),
                SalaryRange = pay?.Value,
                SalaryMin = pay is null ? null : ParseMoney(pay.Groups["min"].Value),
                SalaryMax = pay is null ? null : ParseMoney(pay.Groups["max"].Success ? pay.Groups["max"].Value : pay.Groups["min"].Value),
                SalaryCurrency = pay is null ? null : "CAD",
                SalaryPeriod = pay?.Groups["period"].Value.ToLowerInvariant(),
            });
        }
        return (results, total);
    }

    private static int? ParseMoney(string s) =>
        decimal.TryParse(s.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? (int)Math.Round(d) : null;

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return Regex.Replace(text.Trim(), @"\s+", " ").Trim();
    }
}
