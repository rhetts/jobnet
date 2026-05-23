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

    // Final-URL patterns. Order: most specific first. Negative lookahead `(?!embed\b)` on
    // Greenhouse prevents capturing the literal word "embed" as a slug when the URL is
    // `boards.greenhouse.io/embed/<something>` without the canonical `/embed/job_board?for=` prefix.
    private static readonly (Regex Pattern, string AtsType)[] UrlPatterns =
    {
        (new Regex(@"^https?://(job-boards|boards|apply)\.greenhouse\.io/(?!embed\b)(?<slug>[a-z0-9-]+)", RegexOptions.IgnoreCase), "greenhouse"),
        (new Regex(@"^https?://jobs\.lever\.co/(?<slug>[a-z0-9-]+)",                                                                RegexOptions.IgnoreCase), "lever"),
        (new Regex(@"^https?://jobs\.ashbyhq\.com/(?<slug>[a-z0-9-]+)",                                                             RegexOptions.IgnoreCase), "ashby"),
        (new Regex(@"^https?://apply\.workable\.com/(?<slug>[a-z0-9-]+)",                                                           RegexOptions.IgnoreCase), "workable"),
        (new Regex(@"^https?://(?<slug>[a-z0-9-]+)\.workable\.com",                                                                 RegexOptions.IgnoreCase), "workable"),
        (new Regex(@"^https?://careers\.smartrecruiters\.com/(?<slug>[a-zA-Z0-9-]+)",                                               RegexOptions.IgnoreCase), "smartrecruiters"),
        (new Regex(@"^https?://(?<slug>[a-z0-9-]+)\.recruitee\.com",                                                                RegexOptions.IgnoreCase), "recruitee"),
    };

    // HTML fingerprints — found in the page body when a company embeds a third-party board.
    // Greenhouse pattern: the `(?:embed/job_board\?for=)?` prefix is optional, but the slug
    // capture has a `(?!embed[/\?\s])` guard so we never mistake a partial-match `/embed/...`
    // for a slug. Bug history: previously captured "embed" as the slug, leading to 404s.
    private static readonly (Regex Pattern, string AtsType)[] HtmlPatterns =
    {
        (new Regex(@"(?:src|href)=[""']https?://(?:job-boards|boards|apply)\.greenhouse\.io/(?:embed/job_board\?for=)?(?!embed[/\?\s])(?<slug>[a-z0-9-]+)", RegexOptions.IgnoreCase), "greenhouse"),
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

    // Layer 3: ATS URLs that may appear anywhere in HTML — including inside <script> bodies,
    // JSON config blobs, and inline JS strings (e.g. Trulioo embeds the Ashby URL inside a
    // script tag, not as an iframe). Matches are SOFT — we verify the slug against the ATS
    // public API before persisting so we don't get fooled by a documentation or blog mention.
    private static readonly (Regex Pattern, string AtsType)[] ScriptUrlPatterns =
    {
        (new Regex(@"https?://jobs\.ashbyhq\.com/(?<slug>[a-z0-9][a-z0-9-]{1,60})",                  RegexOptions.IgnoreCase), "ashby"),
        (new Regex(@"https?://api\.ashbyhq\.com/posting-api/job-board/(?<slug>[a-z0-9][a-z0-9-]{1,60})", RegexOptions.IgnoreCase), "ashby"),
        (new Regex(@"https?://(?:job-boards|boards|apply)\.greenhouse\.io/(?:embed/job_board\?for=)?(?!embed[/\?\s])(?<slug>[a-z0-9][a-z0-9-]{1,60})", RegexOptions.IgnoreCase), "greenhouse"),
        (new Regex(@"https?://boards-api\.greenhouse\.io/v1/boards/(?<slug>[a-z0-9][a-z0-9-]{1,60})", RegexOptions.IgnoreCase), "greenhouse"),
        (new Regex(@"https?://jobs\.lever\.co/(?<slug>[a-z0-9][a-z0-9-]{1,60})",                      RegexOptions.IgnoreCase), "lever"),
        (new Regex(@"https?://api\.lever\.co/v0/postings/(?<slug>[a-z0-9][a-z0-9-]{1,60})",           RegexOptions.IgnoreCase), "lever"),
        (new Regex(@"https?://apply\.workable\.com/(?<slug>[a-z0-9][a-z0-9-]{1,60})",                 RegexOptions.IgnoreCase), "workable"),
        (new Regex(@"https?://careers\.smartrecruiters\.com/(?<slug>[a-zA-Z0-9][a-zA-Z0-9-]{1,60})",  RegexOptions.IgnoreCase), "smartrecruiters"),
        (new Regex(@"https?://(?<slug>[a-z0-9][a-z0-9-]{1,60})\.recruitee\.com",                      RegexOptions.IgnoreCase), "recruitee"),
    };

    // Layer 2: anchor text patterns that suggest the link points to the real jobs page.
    // Many marketing /careers pages have a "View open jobs" button that links to the actual
    // ATS embed page. We follow up to a few of these and re-probe.
    private static readonly Regex AnchorJobsHintRe = new(
        @"\b(view\s+(?:all\s+)?(?:open\s+)?(?:jobs|positions|roles)|see\s+(?:all\s+)?(?:open\s+)?(?:jobs|positions|roles)|browse\s+(?:jobs|roles|positions)|open\s+(?:positions|roles)|current\s+open(?:ings|\s+positions)|apply\s+now|join\s+(?:the\s+)?team|join\s+us|we['’]re\s+hiring|explore\s+careers|career\s+opportunities|all\s+jobs)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AnchorTagRe = new(
        @"<a\b[^>]*\bhref\s*=\s*[""'](?<href>[^""']+)[""'][^>]*>(?<text>.{0,200}?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public async Task<AtsDetectionResult> DetectViaHttpAsync(Company company, CancellationToken ct = default)
    {
        // Probe candidate URLs over plain HTTP, accept confirmed (URL or HTML pattern with slug).
        // Track hint-only matches to fall back to slug-guess verification.
        string? hintOnlyAts = null;
        string? hintUrl = null;
        // Collect HTML bodies from candidates that returned 200 but no ATS hit — Layer 2 will
        // scan their anchors for "view open jobs"-style links and follow one hop. Limited to a
        // few bodies so we don't chew memory or do excess work.
        var anchorFollowPool = new List<(string Url, string Body)>();
        foreach (var candidate in CandidateUrls(company))
        {
            ct.ThrowIfCancellationRequested();
            var result = await ProbeUrlAsync(candidate, ct, anchorFollowPool);
            if (result.AtsType is not null && result.AtsSlug is not null) return result;
            if (result.AtsType is not null && hintOnlyAts is null)
            {
                hintOnlyAts = result.AtsType;
                hintUrl = result.ResolvedCareersUrl ?? candidate;
            }
        }

        // Layer 2: follow promising anchors on pages that returned no ATS marker themselves.
        // Bounded: at most 5 follows total across all candidates, depth 1 (no recursion).
        var followsTried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var followsRemaining = 5;
        foreach (var (sourceUrl, body) in anchorFollowPool)
        {
            if (followsRemaining <= 0) break;
            ct.ThrowIfCancellationRequested();
            foreach (var hintUrl2 in ExtractJobsHintAnchors(body, sourceUrl))
            {
                if (followsRemaining <= 0) break;
                if (!followsTried.Add(hintUrl2)) continue;
                followsRemaining--;
                var followResult = await ProbeUrlAsync(hintUrl2, ct, anchorFollowPool: null);
                if (followResult.AtsType is not null && followResult.AtsSlug is not null)
                {
                    return new AtsDetectionResult
                    {
                        AtsType = followResult.AtsType,
                        AtsSlug = followResult.AtsSlug,
                        ResolvedCareersUrl = followResult.ResolvedCareersUrl ?? hintUrl2,
                        Source = "anchor_follow",
                        Notes = $"followed link from {sourceUrl} → {hintUrl2}; source: {followResult.Source}"
                    };
                }
                if (followResult.AtsType is not null && hintOnlyAts is null)
                {
                    hintOnlyAts = followResult.AtsType;
                    hintUrl = followResult.ResolvedCareersUrl ?? hintUrl2;
                }
            }
        }

        // Hint-only detection: guess the slug from the domain stem and verify against the ATS API.
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

        return new AtsDetectionResult { Source = "none", Notes = "no ATS fingerprint via HTTP probing" };
    }

    public async Task<AtsDetectionResult> DetectAsync(Company company, CancellationToken ct = default)
    {
        var http = await DetectViaHttpAsync(company, ct);
        if (http.AtsType is not null && http.AtsSlug is not null) return http;

        // Last resort: render the most likely candidate with Playwright (JS-aware) and re-run pattern matching.
        // Catches JS-rendered ATS embeds (Klue-style) where static HTML didn't show the fingerprint.
        var jsResult = await ProbeWithPlaywrightAsync(company, ct);
        if (jsResult is not null) return jsResult;

        return http;
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
            if (!rendered.Success && rendered.NetworkRequests.Count == 0) return null;

            // **The big win**: scan captured XHR/fetch URLs for ATS API patterns.
            // Many JS-rendered careers pages load jobs from an ATS API in the background;
            // the API URL is in the network log even when it's invisible in the DOM.
            foreach (var req in rendered.NetworkRequests)
            {
                foreach (var (pattern, ats) in UrlPatterns)
                {
                    var m = pattern.Match(req.Url);
                    if (m.Success)
                    {
                        return new AtsDetectionResult
                        {
                            AtsType = ats,
                            AtsSlug = m.Groups["slug"].Value.ToLowerInvariant(),
                            ResolvedCareersUrl = rendered.FinalUrl,
                            Source = "playwright_network",
                            Notes = $"caught via observed XHR to {req.Url}"
                        };
                    }
                }
                // Also catch the boards-api endpoint directly (the most reliable signal)
                var apiMatch = System.Text.RegularExpressions.Regex.Match(req.Url,
                    @"^https?://(?:boards-api\.greenhouse\.io/v1/boards|api\.lever\.co/v0/postings|api\.ashbyhq\.com/posting-api/job-board)/(?<slug>[a-zA-Z0-9-]+)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (apiMatch.Success)
                {
                    var atsType = req.Url.Contains("greenhouse.io") ? "greenhouse"
                                : req.Url.Contains("lever.co")       ? "lever"
                                : "ashby";
                    return new AtsDetectionResult
                    {
                        AtsType = atsType,
                        AtsSlug = apiMatch.Groups["slug"].Value.ToLowerInvariant(),
                        ResolvedCareersUrl = rendered.FinalUrl,
                        Source = "playwright_network_api",
                        Notes = $"ATS API endpoint observed: {req.Url}"
                    };
                }
            }

            if (string.IsNullOrEmpty(rendered.Html)) return null;

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

    private async Task<AtsDetectionResult> ProbeUrlAsync(string url, CancellationToken ct,
                                                          List<(string Url, string Body)>? anchorFollowPool = null)
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

        // Layer 3: scan ALL body content (including inline script tags / JSON blobs) for ATS
        // URLs that aren't inside a src=/href= attribute. Trulioo embeds the Ashby URL inside
        // a script body, which HtmlPatterns misses. These are softer matches, so we VERIFY the
        // slug against the ATS public API before returning a confirmed hit.
        foreach (var (pattern, ats) in ScriptUrlPatterns)
        {
            var m = pattern.Match(body);
            if (!m.Success) continue;
            var slug = m.Groups["slug"].Value.ToLowerInvariant();
            if (string.IsNullOrEmpty(slug)) continue;
            if (await VerifySlugAsync(ats, slug, ct))
            {
                return new AtsDetectionResult
                {
                    AtsType = ats, AtsSlug = slug,
                    ResolvedCareersUrl = finalUrl,
                    Source = "script_url_verified",
                    Notes = $"Found {ats} URL in script body at {finalUrl}; verified slug '{slug}' against ATS API"
                };
            }
        }

        // Markerless hint: page mentions an ATS but has no URL+slug pattern. Return hint so caller
        // can slug-guess. ALSO stash the body in the anchor-follow pool — slug guess can fail, and
        // a "View open jobs" link on the same page may lead to a page where the slug IS visible.
        foreach (var (pattern, ats) in HintOnlyPatterns)
        {
            if (pattern.IsMatch(body))
            {
                if (anchorFollowPool is not null && anchorFollowPool.Count < 4)
                    anchorFollowPool.Add((finalUrl, body));
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

        // Layer 2 plumbing: this page returned 200 with no ATS signal. Stash its body so the
        // caller can scan anchors for "view jobs"-type links and follow them on a second pass.
        // Capped at a few entries to keep memory bounded — first wins.
        if (anchorFollowPool is not null && anchorFollowPool.Count < 4)
            anchorFollowPool.Add((finalUrl, body));

        return new AtsDetectionResult { Source = "none", Notes = $"no fingerprint at {finalUrl}", ResolvedCareersUrl = finalUrl };
    }

    /// <summary>Layer 2: scan a page's anchors for "view open jobs", "apply now", etc. links and
    /// return the same-host hrefs (up to <paramref name="max"/>) for follow-up probing. Resolves
    /// relative URLs against <paramref name="baseUrl"/>. Skips links that match the current page
    /// or external sites (to keep crawl bounded).</summary>
    private static IReadOnlyList<string> ExtractJobsHintAnchors(string html, string baseUrl, int max = 4)
    {
        var results = new List<string>();
        if (string.IsNullOrEmpty(html)) return results;

        Uri? baseUri = null;
        Uri.TryCreate(baseUrl, UriKind.Absolute, out baseUri);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AnchorTagRe.Matches(html))
        {
            var href = m.Groups["href"].Value.Trim();
            var rawText = m.Groups["text"].Value;
            // Strip any inner tags from the anchor text (button > span > text patterns are common).
            var text = System.Text.RegularExpressions.Regex.Replace(rawText, @"<[^>]+>", " ");
            text = System.Net.WebUtility.HtmlDecode(text);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

            if (!AnchorJobsHintRe.IsMatch(text)) continue;
            if (string.IsNullOrEmpty(href) || href.StartsWith("#") || href.StartsWith("mailto:") || href.StartsWith("tel:") || href.StartsWith("javascript:"))
                continue;

            string? resolved = null;
            if (Uri.IsWellFormedUriString(href, UriKind.Absolute))
                resolved = href;
            else if (baseUri is not null && Uri.TryCreate(baseUri, href, out var combined))
                resolved = combined.ToString();
            if (resolved is null) continue;

            // Same-host only — don't chase external job aggregators (LinkedIn etc.) from anchor follow.
            if (baseUri is not null && Uri.TryCreate(resolved, UriKind.Absolute, out var ru))
            {
                if (!string.Equals(ru.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
                    && !ru.Host.EndsWith("." + baseUri.Host, StringComparison.OrdinalIgnoreCase)
                    && !baseUri.Host.EndsWith("." + ru.Host, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            if (string.Equals(resolved, baseUrl, StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(resolved)) continue;

            results.Add(resolved);
            if (results.Count >= max) break;
        }
        return results;
    }

    private static IEnumerable<string> CandidateUrls(Company company)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(company.CareersUrl) && seen.Add(company.CareersUrl))
            yield return company.CareersUrl;

        // Layer 1: subdomains. Cover the common "careers / jobs / apply / etc on subdomain" pattern.
        foreach (var sub in new[] { "careers", "jobs", "join", "work", "apply", "openings" })
        {
            var u = $"https://{sub}.{company.Domain}";
            if (seen.Add(u)) yield return u;
        }

        var bases = new List<string>();
        if (!string.IsNullOrWhiteSpace(company.WebsiteUrl)) bases.Add(company.WebsiteUrl.TrimEnd('/'));
        bases.Add($"https://{company.Domain}");
        bases.Add($"https://www.{company.Domain}");

        // Layer 1: path suffixes. Trulioo's `/apply` was the missing one — added along with several
        // other common conventions observed in the wild. Order: most specific first to reduce
        // false positives on early hits.
        var suffixes = new[]
        {
            "/careers", "/jobs", "/apply",
            "/careers/jobs", "/jobs/all", "/jobs/openings",
            "/company/careers", "/company/jobs",
            "/about/careers", "/about/jobs",
            "/openings", "/open-positions", "/positions",
            "/hiring", "/we-are-hiring",
            "/join-us", "/join", "/team/careers", "/team/jobs",
        };

        foreach (var b in bases)
        {
            foreach (var suffix in suffixes)
            {
                var u = b + suffix;
                if (seen.Add(u)) yield return u;
            }
            if (seen.Add(b)) yield return b;
        }
    }
}
