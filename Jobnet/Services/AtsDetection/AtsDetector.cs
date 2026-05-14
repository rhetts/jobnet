using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Models;
using Jobnet.Services.ApiUsage;
using Jobnet.Services.RateLimit;

namespace Jobnet.Services.AtsDetection;

/// <summary>
/// Detects which ATS (Greenhouse, Lever, Ashby, Workable, SmartRecruiters, Recruitee) a company uses,
/// by following redirects from candidate careers URLs and pattern-matching the final URL, with HTML
/// fingerprinting as fallback.
/// </summary>
public sealed class AtsDetector : IAtsDetector
{
    public const string Provider = "http_fetch";

    private readonly HttpClient _http;
    private readonly IApiUsageTracker _usage;
    private readonly IRateLimiter _rateLimiter;

    public AtsDetector(HttpClient http, IApiUsageTracker usage, IRateLimiter rateLimiter)
    {
        _http = http;
        _usage = usage;
        _rateLimiter = rateLimiter;
    }

    // Final-URL patterns. Order: most specific first.
    private static readonly (Regex Pattern, string AtsType)[] UrlPatterns =
    {
        (new Regex(@"^https?://(job-boards|boards|apply)\.greenhouse\.io/(?<slug>[a-z0-9-]+)", RegexOptions.IgnoreCase),         "greenhouse"),
        (new Regex(@"^https?://jobs\.lever\.co/(?<slug>[a-z0-9-]+)",                                                              RegexOptions.IgnoreCase), "lever"),
        (new Regex(@"^https?://jobs\.ashbyhq\.com/(?<slug>[a-z0-9-]+)",                                                           RegexOptions.IgnoreCase), "ashby"),
        (new Regex(@"^https?://apply\.workable\.com/(?<slug>[a-z0-9-]+)",                                                        RegexOptions.IgnoreCase), "workable"),
        (new Regex(@"^https?://(?<slug>[a-z0-9-]+)\.workable\.com",                                                              RegexOptions.IgnoreCase), "workable"),
        (new Regex(@"^https?://careers\.smartrecruiters\.com/(?<slug>[a-zA-Z0-9-]+)",                                            RegexOptions.IgnoreCase), "smartrecruiters"),
        (new Regex(@"^https?://(?<slug>[a-z0-9-]+)\.recruitee\.com",                                                             RegexOptions.IgnoreCase), "recruitee"),
    };

    // HTML fingerprints — found in the page body when a company embeds a third-party board.
    private static readonly (Regex Pattern, string AtsType)[] HtmlPatterns =
    {
        (new Regex(@"(?:src|href)=[""']https?://(?:job-boards|boards|apply)\.greenhouse\.io/(?:embed/job_board\?for=)?(?<slug>[a-z0-9-]+)", RegexOptions.IgnoreCase), "greenhouse"),
        (new Regex(@"(?:src|href)=[""']https?://jobs\.lever\.co/(?<slug>[a-z0-9-]+)",                                                       RegexOptions.IgnoreCase), "lever"),
        (new Regex(@"(?:src|href)=[""']https?://jobs\.ashbyhq\.com/(?<slug>[a-z0-9-]+)",                                                    RegexOptions.IgnoreCase), "ashby"),
        (new Regex(@"https?://api\.ashbyhq\.com/posting-api/job-board/(?<slug>[a-z0-9-]+)",                                                 RegexOptions.IgnoreCase), "ashby"),
        (new Regex(@"(?:src|href)=[""']https?://apply\.workable\.com/(?<slug>[a-z0-9-]+)",                                                  RegexOptions.IgnoreCase), "workable"),
        (new Regex(@"(?:src|href)=[""']https?://careers\.smartrecruiters\.com/(?<slug>[a-zA-Z0-9-]+)",                                      RegexOptions.IgnoreCase), "smartrecruiters"),
        (new Regex(@"(?:src|href)=[""']https?://(?<slug>[a-z0-9-]+)\.recruitee\.com",                                                       RegexOptions.IgnoreCase), "recruitee"),
    };

    public async Task<AtsDetectionResult> DetectAsync(Company company, CancellationToken ct = default)
    {
        foreach (var candidate in CandidateUrls(company))
        {
            ct.ThrowIfCancellationRequested();
            var result = await ProbeUrlAsync(candidate, ct);
            if (result.AtsType is not null) return result;
        }
        return new AtsDetectionResult { Source = "none", Notes = "no ATS fingerprint found at any candidate URL" };
    }

    private async Task<AtsDetectionResult> ProbeUrlAsync(string url, CancellationToken ct)
    {
        await _rateLimiter.WaitAsync(Provider, ct);
        _usage.RecordCall(Provider);

        HttpResponseMessage? response;
        try
        {
            response = await _http.GetAsync(url, ct);
        }
        catch (Exception ex)
        {
            return new AtsDetectionResult { Source = "none", Notes = $"fetch failed: {ex.Message}" };
        }

        using var _ = response;
        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;

        // Try URL pattern first
        foreach (var (pattern, ats) in UrlPatterns)
        {
            var m = pattern.Match(finalUrl);
            if (m.Success)
            {
                return new AtsDetectionResult
                {
                    AtsType = ats,
                    AtsSlug = m.Groups["slug"].Value.ToLowerInvariant(),
                    ResolvedCareersUrl = finalUrl,
                    Source = "redirect",
                };
            }
        }

        // Fallback: scan HTML body for embedded ATS markers
        if (!response.IsSuccessStatusCode)
            return new AtsDetectionResult { Source = "none", Notes = $"HTTP {(int)response.StatusCode} at {finalUrl}" };

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return new AtsDetectionResult { Source = "none", Notes = $"could not read body of {finalUrl}" };
        }

        foreach (var (pattern, ats) in HtmlPatterns)
        {
            var m = pattern.Match(body);
            if (m.Success)
            {
                return new AtsDetectionResult
                {
                    AtsType = ats,
                    AtsSlug = m.Groups["slug"].Value.ToLowerInvariant(),
                    ResolvedCareersUrl = finalUrl,
                    Source = "html_fingerprint",
                };
            }
        }

        return new AtsDetectionResult { Source = "none", Notes = $"no fingerprint at {finalUrl}", ResolvedCareersUrl = finalUrl };
    }

    private static IEnumerable<string> CandidateUrls(Company company)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(company.CareersUrl) && seen.Add(company.CareersUrl))
            yield return company.CareersUrl;

        var bases = new List<string>();
        if (!string.IsNullOrWhiteSpace(company.WebsiteUrl)) bases.Add(company.WebsiteUrl.TrimEnd('/'));
        bases.Add($"https://{company.Domain}");
        bases.Add($"https://www.{company.Domain}");

        foreach (var b in bases)
        {
            foreach (var suffix in new[] { "/careers", "/jobs", "/careers/jobs", "/company/careers", "/about/careers" })
            {
                var u = b + suffix;
                if (seen.Add(u)) yield return u;
            }
            if (seen.Add(b)) yield return b;
        }
    }
}
