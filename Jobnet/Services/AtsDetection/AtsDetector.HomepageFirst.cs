using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Models;

namespace Jobnet.Services.AtsDetection;

/// <summary>
/// Homepage-first detection — what a person does: open the company's homepage, click its
/// Careers link, and look at where that leads. A handful of fetches instead of the legacy flow's
/// ~46 guessed URLs plus five browser renders (which took 40s–2min per company, most of it on
/// guesses that 404'd or redirected back to the homepage).
///
/// Order:
///   1. Stored careers_url, if any.
///   2. Homepage (website_url, then bare domain, then www.). Unreachable → stop: the site is dead,
///      no amount of guessing will find a careers page on it. Redirects to another domain → stop
///      too: the company was renamed or acquired, and the new site's ATS isn't this company's.
///   3. Up to 3 careers-looking links from the homepage nav/footer, then one more hop from each
///      ("View open roles" buttons on a careers landing page).
///   4. Browser: render the homepage only if plain HTML had no careers link (JS-built nav), and the
///      best careers page if plain HTML showed no ATS (JS-loaded job widget). At most 2 renders.
///   5. Only if no careers link turned up anywhere: a 3-URL guess list (careers./, /careers, /jobs).
///   6. Hint-only slug guessing, as before.
/// </summary>
public sealed partial class AtsDetector
{
    private const int MaxCareersLinks = 3;
    private const int MaxSecondHop = 3;

    // Anchor text that names a careers page. Broader than AnchorJobsHintRe (which wants "view open
    // jobs"-style call-to-action text) because nav/footer links are usually one word.
    private static readonly Regex CareersTextRe = new(
        @"\b(careers?|jobs?|job\s+openings|join\s+(?:us|our\s+team|the\s+team)|work\s+(?:with|at|for)\s+us|we['’]re\s+hiring|hiring|open\s+(?:roles|positions)|carri[eè]res?|emplois?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Same idea on the href path, for icon/image links with no useful text.
    private static readonly Regex CareersPathRe = new(
        @"/(careers?|jobs?|join(?:-us|-our-team)?|hiring|work-with-us|carrieres|emplois)(?:[/?#.-]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CareersHostRe = new(@"^(careers?|jobs|apply|work)\.", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public Task<AtsDetectionResult> DetectAsync(Company company, CancellationToken ct = default)
        => DetectHomepageFirstAsync(company, useBrowser: true, ct);

    public Task<AtsDetectionResult> DetectViaHttpAsync(Company company, CancellationToken ct = default)
        => DetectHomepageFirstAsync(company, useBrowser: false, ct);

    public Task<AtsDetectionResult> DetectWithAsync(Company company, AtsDetectionMethod method, CancellationToken ct = default)
        => method == AtsDetectionMethod.Legacy ? DetectLegacyAsync(company, ct) : DetectAsync(company, ct);

    /// <summary>Collects what happened at each step so a miss can be explained (Notes) and the
    /// eval command can show the path taken.</summary>
    private sealed class Trace
    {
        public readonly List<string> Steps = new();
        public string? HintAts;
        public string? HintUrl;
        public void Add(string s) => Steps.Add(s);
        public override string ToString() => string.Join(" → ", Steps);
    }

    private async Task<AtsDetectionResult> DetectHomepageFirstAsync(Company company, bool useBrowser, CancellationToken ct)
    {
        var trace = new Trace();
        var scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // final URLs already looked at

        AtsDetectionResult Hit(AtsDetectionResult r, string how) => new()
        {
            AtsType = r.AtsType, AtsSlug = r.AtsSlug, ResolvedCareersUrl = r.ResolvedCareersUrl,
            Source = $"{how}:{r.Source}",
            Notes = trace.ToString(),
        };

        // 1. A careers URL we already know about.
        if (!string.IsNullOrWhiteSpace(company.CareersUrl))
        {
            var (r, _) = await ProbeAsync(company.CareersUrl!, "careers_url", trace, scanned, ct);
            if (IsHit(r)) return Hit(r, "careers_url");
        }

        // 2. Homepage.
        PageInfo? home = null;
        var blocked = (string?)null;   // answered, but refused plain HTTP (bot protection)
        var homeRendered = false;
        foreach (var url in HomepageCandidates(company))
        {
            var (r, info) = await ProbeAsync(url, "home", trace, scanned, ct);
            if (IsHit(r) && MovedDomainNote(company, info.FinalUrl ?? url) is null) return Hit(r, "homepage");
            if (info.Body is not null) { home = info; break; }
            if (info.Status is 403 or 429 or 503) blocked ??= info.FinalUrl ?? url;
        }
        if (home is null && blocked is not null && useBrowser)
        {
            // A real browser often gets past the challenge page that plain HTTP hits.
            var rendered = await RenderAsync(blocked, "render_home", trace, ct);
            if (rendered is not null)
            {
                var r = await ScanRenderedAsync(company, rendered, ct);
                if (IsHit(r)) return Hit(r!, "render_home");
                homeRendered = true;
                if (!string.IsNullOrEmpty(rendered.Html))
                    home = new PageInfo { FinalUrl = rendered.FinalUrl, Body = rendered.Html, Status = rendered.HttpStatus };
            }
        }
        if (home is null)
        {
            // Every homepage variant failed DNS / connect / 4xx-5xx. Guessing paths on a dead site
            // is the single biggest time sink of the legacy flow, so stop here.
            return new AtsDetectionResult
            {
                Source = "site_unreachable",
                Notes = trace.ToString(),
            };
        }
        var homeUrl = home.FinalUrl!;
        var movedNote = MovedDomainNote(company, homeUrl);
        if (movedNote is not null)
        {
            // Renamed or acquired (apisero.com → nttdata.com). Whatever ATS the new site uses
            // belongs to the new owner — Apisero's search found NTT's *data-centre* subsidiary
            // board two hops in. Report it and let the user update the domain instead.
            trace.Add(movedNote);
            return new AtsDetectionResult { Source = "site_moved", Notes = trace.ToString() };
        }

        // 3. Careers links from the homepage, then one more hop from each.
        var careersLinks = ExtractCareersLinks(home.Body!, homeUrl, company.Domain);
        var firstCareersPage = (string?)null;
        var hop2 = new List<string>();
        foreach (var link in careersLinks)
        {
            var (r, info) = await ProbeAsync(link, "careers", trace, scanned, ct);
            if (IsHit(r)) return Hit(r, "careers_link");
            if (info.Body is null || info.Redundant) continue;
            firstCareersPage ??= info.FinalUrl;
            foreach (var next in ExtractCareersLinks(info.Body, info.FinalUrl!, company.Domain)
                                    .Concat(ExtractJobsHintAnchors(info.Body, info.FinalUrl!)))
                if (!hop2.Contains(next, StringComparer.OrdinalIgnoreCase)) hop2.Add(next);
        }
        foreach (var link in hop2.Take(MaxSecondHop))
        {
            var (r, _) = await ProbeAsync(link, "hop2", trace, scanned, ct);
            if (IsHit(r)) return Hit(r, "careers_link_hop2");
        }

        // 4. Browser — only where plain HTML could plausibly have missed something.
        if (useBrowser)
        {
            if (careersLinks.Count == 0 && !homeRendered)
            {
                // No careers link in the static HTML: the nav is probably built by JavaScript.
                var rendered = await RenderAsync(homeUrl, "render_home", trace, ct);
                if (rendered is not null)
                {
                    var r = await ScanRenderedAsync(company, rendered, ct);
                    if (IsHit(r)) return Hit(r!, "render_home");
                    var renderedLinks = ExtractCareersLinks(rendered.Html, rendered.FinalUrl, company.Domain);
                    foreach (var link in renderedLinks)
                    {
                        var (r2, info) = await ProbeAsync(link, "careers(js)", trace, scanned, ct);
                        if (IsHit(r2)) return Hit(r2, "careers_link_js");
                        if (info.Body is not null && !info.Redundant) firstCareersPage ??= info.FinalUrl;
                    }
                    careersLinks = renderedLinks;
                }
            }
            if (firstCareersPage is not null)
            {
                // Careers page exists but static HTML showed no ATS: the job list is likely a JS widget.
                var rendered = await RenderAsync(firstCareersPage, "render_careers", trace, ct);
                if (rendered is not null)
                {
                    var r = await ScanRenderedAsync(company, rendered, ct);
                    if (IsHit(r)) return Hit(r!, "render_careers");
                }
            }
        }

        // 5. Small guess list, only when the site gave us nothing to follow.
        if (careersLinks.Count == 0 && firstCareersPage is null)
        {
            var root = new Uri(homeUrl).GetLeftPart(UriPartial.Authority);
            foreach (var guess in new[] { $"https://careers.{company.Domain}", $"{root}/careers", $"{root}/jobs" })
            {
                var (r, _) = await ProbeAsync(guess, "guess", trace, scanned, ct);
                if (IsHit(r)) return Hit(r, "guess");
            }
        }

        // 6. A page mentioned an ATS without showing the slug — try slugs from the domain.
        if (trace.HintAts is not null)
        {
            foreach (var guess in SlugGuesses(company))
            {
                if (await VerifySlugAsync(trace.HintAts, guess, ct))
                {
                    trace.Add($"slug guess '{guess}' verified");
                    return new AtsDetectionResult
                    {
                        AtsType = trace.HintAts, AtsSlug = guess, ResolvedCareersUrl = trace.HintUrl,
                        Source = "slug_guess_verified", Notes = trace.ToString(),
                    };
                }
            }
            return new AtsDetectionResult
            {
                AtsType = trace.HintAts, ResolvedCareersUrl = trace.HintUrl,
                Source = "hint_only", Notes = trace.ToString(),
            };
        }

        return new AtsDetectionResult
        {
            Source = careersLinks.Count == 0 && firstCareersPage is null ? "no_careers_page" : "none",
            ResolvedCareersUrl = firstCareersPage,
            Notes = trace.ToString(),
        };
    }

    private static bool IsHit(AtsDetectionResult? r) => r?.AtsType is not null && r.AtsSlug is not null;

    /// <summary>Fetch + ATS-scan one URL, skipping it if it's already been scanned (directly or as
    /// the landing page of a redirect). <see cref="PageInfo.Redundant"/> is set when the fetch landed
    /// on a page we'd already seen — the "every unknown path redirects to the homepage" case.</summary>
    private async Task<(AtsDetectionResult Result, PageInfo Info)> ProbeAsync(string url, string label,
        Trace trace, HashSet<string> scanned, CancellationToken ct)
    {
        var info = new PageInfo();
        if (scanned.Contains(Normalize(url)))
        {
            info.Redundant = true;
            return (new AtsDetectionResult { Source = "none" }, info);
        }
        scanned.Add(Normalize(url));

        var r = await ProbeUrlAsync(url, ct, anchorFollowPool: null, info);
        if (info.FinalUrl is not null && !scanned.Add(Normalize(info.FinalUrl))
            && !string.Equals(Normalize(info.FinalUrl), Normalize(url), StringComparison.OrdinalIgnoreCase))
            info.Redundant = true;

        var outcome = IsHit(r) ? $"{r.AtsType}:{r.AtsSlug}"
                    : info.Error is not null ? "unreachable"
                    : info.Status is int s && s >= 400 ? s.ToString()
                    : info.Redundant ? $"same page as before ({info.FinalUrl})"
                    : info.FinalUrl is not null && !string.Equals(Normalize(info.FinalUrl), Normalize(url), StringComparison.OrdinalIgnoreCase)
                        ? $"→ {info.FinalUrl}" : "no ATS";
        trace.Add($"{label} {url} [{outcome}]");

        if (r.AtsType is not null && r.AtsSlug is null && trace.HintAts is null)
        {
            trace.HintAts = r.AtsType;
            trace.HintUrl = r.ResolvedCareersUrl ?? url;
        }
        return (r, info);
    }

    private async Task<Playwright.PlaywrightFetchResult?> RenderAsync(string url, string label, Trace trace, CancellationToken ct)
    {
        try
        {
            var rendered = await _playwright.FetchAsync(url, ct);
            trace.Add($"{label} {url} [{(rendered.Success ? "rendered" : rendered.Error ?? "failed")}]");
            return rendered.Success || rendered.NetworkRequests.Count > 0 ? rendered : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            trace.Add($"{label} {url} [{ex.GetType().Name}]");
            return null;
        }
    }

    private static IEnumerable<string> HomepageCandidates(Company company)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(company.WebsiteUrl) && seen.Add(company.WebsiteUrl!.TrimEnd('/')))
            yield return company.WebsiteUrl!.TrimEnd('/');
        foreach (var u in new[] { $"https://{company.Domain}", $"https://www.{company.Domain}" })
            if (seen.Add(u)) yield return u;
    }

    /// <summary>Non-null when the homepage redirected to a different domain — the company was
    /// renamed or acquired (dashhudson.com → dashsocial.com), worth surfacing.</summary>
    public static string? MovedDomainNote(Company company, string finalUrl)
    {
        if (!Uri.TryCreate(finalUrl, UriKind.Absolute, out var u)) return null;
        var site = RegistrableDomain(u.Host);
        return site == RegistrableDomain(company.Domain)
            ? null
            : $"site moved: {company.Domain} redirects to {site}";
    }

    /// <summary>The registered name of a host: last two labels, or three under two-part suffixes
    /// like co.uk / com.au. Compares app.dealroom.co with dealroom.co as the same site.</summary>
    public static string RegistrableDomain(string host)
    {
        var labels = host.Trim().TrimEnd('.').ToLowerInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length <= 2) return string.Join('.', labels);
        var twoPartSuffix = labels[^1].Length == 2 && labels[^2] is "co" or "com" or "org" or "net" or "ac" or "gov";
        return string.Join('.', labels[^(twoPartSuffix ? 3 : 2)..]);
    }

    /// <summary>Links on a page that look like they lead to a careers page, best first. Only links on
    /// the company's own domain (any subdomain) — a careers link to another domain is a parent or
    /// sister company's site (careers.nttdata.com on an acquired company's page), not this one's.
    /// Links straight to a known ATS are left to the HTML fingerprint scan, which has already run
    /// on this page.</summary>
    public static IReadOnlyList<string> ExtractCareersLinks(string html, string pageUrl, string companyDomain,
                                                              int max = MaxCareersLinks)
    {
        var scored = new List<(string Url, int Score, int Order)>();
        if (string.IsNullOrEmpty(html) || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri)) return Array.Empty<string>();

        var site = RegistrableDomain(companyDomain);
        var order = 0;
        foreach (Match m in AnchorTagRe.Matches(html))
        {
            var href = System.Net.WebUtility.HtmlDecode(m.Groups["href"].Value.Trim());
            if (href.Length == 0 || href.StartsWith('#') || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Uri.TryCreate(baseUri, href, out var target) || (target.Scheme != "http" && target.Scheme != "https")) continue;

            var text = Regex.Replace(m.Groups["text"].Value, "<[^>]+>", " ");
            text = Regex.Replace(System.Net.WebUtility.HtmlDecode(text), @"\s+", " ").Trim();
            var textHit = CareersTextRe.IsMatch(text);
            var pathHit = CareersPathRe.IsMatch(target.AbsolutePath);
            var hostHit = CareersHostRe.IsMatch(target.Host);
            if (!textHit && !pathHit && !hostHit) continue;

            if (RegistrableDomain(target.Host) != site) continue;

            var url = target.GetLeftPart(UriPartial.Query);
            if (string.Equals(Normalize(url), Normalize(pageUrl), StringComparison.OrdinalIgnoreCase)) continue;

            // One-word "Careers"/"Jobs" nav links are the strongest signal; then path/host matches.
            var exact = Regex.IsMatch(text, @"^(careers?|jobs?|join\s+us|we['’]re\s+hiring|carri[eè]res?)$", RegexOptions.IgnoreCase);
            var score = (exact ? 4 : textHit ? 2 : 0) + (pathHit || hostHit ? 2 : 0);
            scored.Add((url, score, order++));
        }

        return scored
            .GroupBy(s => Normalize(s.Url), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Score).First())
            .OrderByDescending(s => s.Score).ThenBy(s => s.Order)
            .Select(s => s.Url)
            .Take(max)
            .ToList();
    }

    private static string Normalize(string url) => url.Trim().TrimEnd('/').Replace("://www.", "://", StringComparison.OrdinalIgnoreCase);

    /// <summary>What a fetch saw, for callers that need more than the ATS verdict.</summary>
    private sealed class PageInfo
    {
        public string? FinalUrl;
        public int? Status;
        public string? Body;
        public string? Error;
        public bool Redundant;
    }
}

public enum AtsDetectionMethod { HomepageFirst, Legacy }
