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
    private readonly Playwright.IPlaywrightFetcher _playwright;

    public AtsDetector(HttpClient http, IApiUsageTracker usage, IRateLimiter rateLimiter, Playwright.IPlaywrightFetcher playwright)
    {
        _http = http;
        _usage = usage;
        _rateLimiter = rateLimiter;
        _playwright = playwright;
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

    // Markerless fingerprints — page hints at an ATS but no URL+slug is in the static HTML.
    // We then try guessing the slug from the company domain and verifying against the ATS public API.
    private static readonly (Regex Pattern, string AtsType)[] HintOnlyPatterns =
    {
        (new Regex(@"data-api=[""']ashby[""']",                                  RegexOptions.IgnoreCase), "ashby"),
        (new Regex(@"jsx-jobs-list|gh-board|greenhouse[_-]board|grnhse",          RegexOptions.IgnoreCase), "greenhouse"),
        (new Regex(@"lever-jobs-list|lever-job-list|leverapp",                    RegexOptions.IgnoreCase), "lever"),
    };

    public async Task<AtsDetectionResult> DetectAsync(Company company, CancellationToken ct = default)
    {
        // First pass: probe candidate URLs, accept confirmed (URL or HTML pattern with slug).
        // Track hint-only matches to fall back to slug-guess verification.
        string? hintOnlyAts = null;
        string? hintUrl = null;
        foreach (var candidate in CandidateUrls(company))
        {
            ct.ThrowIfCancellationRequested();
            var result = await ProbeUrlAsync(candidate, ct);
            if (result.AtsType is not null && result.AtsSlug is not null) return result;
            if (result.AtsType is not null && hintOnlyAts is null)
            {
                hintOnlyAts = result.AtsType;
                hintUrl = result.ResolvedCareersUrl ?? candidate;
            }
        }

        // Fallback: hint-only detection. Guess the slug from the domain stem and verify against the ATS API.
        if (hintOnlyAts is not null)
        {
            foreach (var guess in SlugGuesses(company))
            {
                var ok = await VerifySlugAsync(hintOnlyAts, guess, ct);
                if (ok)
                {
                    return new AtsDetectionResult
                    {
                        AtsType = hintOnlyAts,
                        AtsSlug = guess,
                        ResolvedCareersUrl = hintUrl,
                        Source = "slug_guess_verified",
                        Notes = $"hint found on page; verified slug '{guess}' against ATS API"
                    };
                }
            }
            return new AtsDetectionResult
            {
                AtsType = hintOnlyAts,
                AtsSlug = null,
                ResolvedCareersUrl = hintUrl,
                Source = "hint_only",
                Notes = "ATS hint found but slug could not be guessed; manual entry needed"
            };
        }

        // Last resort: render the most likely candidate with Playwright (JS-aware) and re-run pattern matching.
        // Catches JS-rendered ATS embeds (Klue-style) where static HTML didn't show the fingerprint.
        var jsResult = await ProbeWithPlaywrightAsync(company, ct);
        if (jsResult is not null) return jsResult;

        return new AtsDetectionResult { Source = "none", Notes = "no ATS fingerprint found via static fetch or Playwright render" };
    }

    private async Task<AtsDetectionResult?> ProbeWithPlaywrightAsync(Company company, CancellationToken ct)
    {
        // Try a small set of promising candidates with Playwright (each fetch is slow, keep this short).
        var jsCandidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(company.CareersUrl)) jsCandidates.Add(company.CareersUrl);
        jsCandidates.Add($"https://careers.{company.Domain}");
        jsCandidates.Add($"https://jobs.{company.Domain}");
        var basePrimary = string.IsNullOrEmpty(company.WebsiteUrl) ? $"https://{company.Domain}" : company.WebsiteUrl.TrimEnd('/');
        jsCandidates.Add($"{basePrimary}/careers");
        jsCandidates.Add($"{basePrimary}/jobs");

        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in jsCandidates)
        {
            if (!tried.Add(url)) continue;
            var result = await ProbeOneWithPlaywrightAsync(company, url, ct);
            if (result is not null) return result;
        }
        return null;
    }

    private async Task<AtsDetectionResult?> ProbeOneWithPlaywrightAsync(Company company, string url, CancellationToken ct)
    {
        try
        {
            var rendered = await _playwright.FetchAsync(url, ct);
            if (!rendered.Success || string.IsNullOrEmpty(rendered.Html)) return null;

            // Re-run URL patterns on final URL
            foreach (var (pattern, ats) in UrlPatterns)
            {
                var m = pattern.Match(rendered.FinalUrl);
                if (m.Success)
                {
                    return new AtsDetectionResult
                    {
                        AtsType = ats,
                        AtsSlug = m.Groups["slug"].Value.ToLowerInvariant(),
                        ResolvedCareersUrl = rendered.FinalUrl,
                        Source = "playwright_redirect",
                    };
                }
            }

            // Re-run HTML patterns on rendered DOM
            foreach (var (pattern, ats) in HtmlPatterns)
            {
                var m = pattern.Match(rendered.Html);
                if (m.Success)
                {
                    return new AtsDetectionResult
                    {
                        AtsType = ats,
                        AtsSlug = m.Groups["slug"].Value.ToLowerInvariant(),
                        ResolvedCareersUrl = rendered.FinalUrl,
                        Source = "playwright_html",
                    };
                }
            }

            // Hint-only on rendered DOM → slug-guess + verify
            foreach (var (pattern, ats) in HintOnlyPatterns)
            {
                if (!pattern.IsMatch(rendered.Html)) continue;
                foreach (var guess in SlugGuesses(company))
                {
                    if (await VerifySlugAsync(ats, guess, ct))
                    {
                        return new AtsDetectionResult
                        {
                            AtsType = ats,
                            AtsSlug = guess,
                            ResolvedCareersUrl = rendered.FinalUrl,
                            Source = "playwright_hint_verified",
                            Notes = $"hint found on rendered page; verified slug '{guess}'"
                        };
                    }
                }
            }
        }
        catch
        {
            // Playwright not available or fetch failed; fall through.
        }
        return null;
    }

    private async Task<bool> VerifySlugAsync(string ats, string slug, CancellationToken ct)
    {
        var verifyUrl = ats switch
        {
            "greenhouse"      => $"https://boards-api.greenhouse.io/v1/boards/{slug}/jobs",
            "lever"           => $"https://api.lever.co/v0/postings/{slug}?mode=json&limit=1",
            "ashby"           => $"https://api.ashbyhq.com/posting-api/job-board/{slug}",
            "workable"        => $"https://{slug}.workable.com/api/v3/jobs",
            "smartrecruiters" => $"https://api.smartrecruiters.com/v1/companies/{slug}/postings?limit=1",
            _                 => null,
        };
        if (verifyUrl is null) return false;

        await _rateLimiter.WaitAsync(Provider, ct);
        _usage.RecordCall(Provider);
        try
        {
            using var resp = await _http.GetAsync(verifyUrl, ct);
            if (!resp.IsSuccessStatusCode) return false;
            var body = await resp.Content.ReadAsStringAsync(ct);
            // Lenient: any 200 with non-empty body is a strong signal. (Empty arrays for inactive boards are still valid.)
            return body.Length > 10;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Plausible slug candidates from the company domain — most common ATS slug patterns.</summary>
    private static IEnumerable<string> SlugGuesses(Company company)
    {
        var seen = new HashSet<string>();
        var stem = company.Domain.Split('.')[0].ToLowerInvariant();   // klue.com → klue, my-company.io → my-company
        if (seen.Add(stem)) yield return stem;

        // No-dash variant: blackbird-interactive → blackbirdinteractive
        var nodash = stem.Replace("-", "");
        if (nodash != stem && seen.Add(nodash)) yield return nodash;

        // Name-based: lowercased, spaces → dashes
        var nameSlug = (company.Name ?? "").ToLowerInvariant().Replace(" ", "-");
        if (!string.IsNullOrEmpty(nameSlug) && seen.Add(nameSlug)) yield return nameSlug;

        var nameNoSpace = (company.Name ?? "").ToLowerInvariant().Replace(" ", "");
        if (!string.IsNullOrEmpty(nameNoSpace) && seen.Add(nameNoSpace)) yield return nameNoSpace;
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

        // Markerless hint: page mentions an ATS but has no URL+slug pattern. Return hint so caller can slug-guess.
        foreach (var (pattern, ats) in HintOnlyPatterns)
        {
            if (pattern.IsMatch(body))
            {
                return new AtsDetectionResult
                {
                    AtsType = ats,
                    AtsSlug = null,
                    ResolvedCareersUrl = finalUrl,
                    Source = "html_hint",
                    Notes = $"ATS '{ats}' mentioned on page but no slug visible in static HTML"
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

        // Some companies host careers on a subdomain (e.g. careers.hootsuite.com, jobs.acme.com).
        foreach (var sub in new[] { "careers", "jobs", "join", "work" })
        {
            var u = $"https://{sub}.{company.Domain}";
            if (seen.Add(u)) yield return u;
        }

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
