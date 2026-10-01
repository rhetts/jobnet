using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jobnet.Services.Discovery;

/// <summary>
/// Finds the real company domain behind an ATS job board (jobs.ashbyhq.com/klue → klue.com).
/// The ATS host itself is never the company domain, and boards rarely link the company site as a
/// plain anchor — Ashby and SmartRecruiters carry it inside script data, Lever on the logo — so
/// this scans every absolute URL in the page rather than just anchors (unlike
/// <see cref="DomainResolution.TryResolveExternalDomainAsync"/>, which takes the first external
/// anchor). To keep that from picking up a CDN or a "powered by" link, a candidate must look
/// like the board's own slug (see <see cref="SlugMatchesDomain"/>). No match → null, and the
/// caller skips the hit rather than inserting a junk company.
/// </summary>
public static class AtsBoardDomainResolver
{
    // Also matches JSON-escaped "https:\/\/host" — script data often carries URLs that way.
    private static readonly Regex AbsoluteUrlRe = new(
        @"https?:(?:\\?/){2}(?<host>[a-z0-9](?:[a-z0-9-]*[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]*[a-z0-9])?)+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<string?> ResolveAsync(HttpClient http, Filters.FilterRuleSet filters,
        string atsType, string slug, string boardUrl, CancellationToken ct)
    {
        bool IsBlocked(string host) => filters.IsHostBlocked(host, Models.FilterScope.Discovery);

        // Workable's account API states the company website outright — better than any scrape.
        if (atsType == "workable")
        {
            var fromApi = await FromWorkableAccountAsync(http, slug, ct);
            if (fromApi is not null && !IsBlocked(fromApi)) return fromApi;
        }

        string? fromBoard = null;
        try
        {
            using var resp = await http.GetAsync(boardUrl, ct);
            if (resp.IsSuccessStatusCode)
                fromBoard = PickCompanyDomain(await resp.Content.ReadAsStringAsync(ct), boardUrl, slug, IsBlocked);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* fall through to guessing */ }
        if (fromBoard is not null) return fromBoard;

        // Greenhouse boards only show the website when the company set a logo link, and Workday
        // boards never do. Guess {tenant}.com/.ca/.io instead — but only accept a guess whose own
        // homepage or /careers page links back to this exact board, so a same-named stranger
        // (or a parked domain) can't get in.
        if (atsType is "greenhouse" or "workday")
            return await GuessFromTenantAsync(http, atsType, slug, IsBlocked, ct);
        return null;
    }

    private static readonly string[] GuessTlds = { "com", "ca", "io" };
    private static readonly string[] GuessPaths = { "", "/careers" };

    private static async Task<string?> GuessFromTenantAsync(HttpClient http, string atsType, string slug,
        Func<string, bool> isBlocked, CancellationToken ct)
    {
        var tenant = slug.Split('/')[0].Split('.')[0].ToLowerInvariant();
        if (!Regex.IsMatch(tenant, "^[a-z0-9][a-z0-9-]{1,60}$")) return null;

        foreach (var tld in GuessTlds)
        {
            var domain = $"{tenant}.{tld}";
            if (isBlocked(domain)) continue;
            foreach (var path in GuessPaths)
            {
                try
                {
                    using var resp = await http.GetAsync($"https://{domain}{path}", ct);
                    if (!resp.IsSuccessStatusCode) continue;
                    var html = await resp.Content.ReadAsStringAsync(ct);
                    if (PageLinksBoard(html, atsType, slug)) return domain;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { break; }   // DNS failure / refused — no point trying /careers on it
            }
        }
        return null;
    }

    /// <summary>Does <paramref name="html"/> link to this specific board? Greenhouse: the slug in a
    /// greenhouse.io board, embed (<c>for=slug</c>) or API URL. Workday: the tenant's
    /// myworkdayjobs host (the slug's host part).</summary>
    public static bool PageLinksBoard(string html, string atsType, string slug)
    {
        if (string.IsNullOrEmpty(html) || string.IsNullOrWhiteSpace(slug)) return false;
        if (atsType == "workday")
        {
            var host = slug.Split('/')[0];
            return host.Contains(".myworkdayjobs.com", StringComparison.OrdinalIgnoreCase)
                && html.Contains(host, StringComparison.OrdinalIgnoreCase);
        }
        if (atsType == "greenhouse")
        {
            var s = Regex.Escape(slug);
            return Regex.IsMatch(html,
                $@"greenhouse\.io/(?:v1/boards/|embed/job_board(?:/js)?\?for=)?{s}(?![a-z0-9-])",
                RegexOptions.IgnoreCase);
        }
        return false;
    }

    /// <summary>First URL in <paramref name="html"/> whose host isn't the ATS vendor's, looks like
    /// <paramref name="slug"/>, and isn't filter-blocked. Returns the canonical domain (same
    /// normalisation as <see cref="DomainExtractor"/>, so it lines up with GetByDomain).</summary>
    public static string? PickCompanyDomain(string html, string boardUrl, string slug, Func<string, bool> isBlocked)
    {
        if (string.IsNullOrEmpty(html)) return null;
        if (!Uri.TryCreate(boardUrl, UriKind.Absolute, out var boardUri)) return null;
        var vendorRoot = RootDomain(boardUri.Host.ToLowerInvariant());

        foreach (Match m in AbsoluteUrlRe.Matches(html))
        {
            var extracted = DomainExtractor.Extract($"https://{m.Groups["host"].Value}");
            if (extracted is null) continue;
            var domain = extracted.CanonicalDomain;
            if (domain == vendorRoot || domain.EndsWith("." + vendorRoot, StringComparison.Ordinal)) continue;
            // Slug check before the filter check so unrelated hosts (w3.org, CDNs) don't bump
            // filter_rule hit counts.
            if (!SlugMatchesDomain(slug, domain)) continue;
            if (isBlocked(domain)) continue;
            return domain;
        }
        return null;
    }

    /// <summary>Does <paramref name="domain"/> plausibly belong to the ATS account
    /// <paramref name="slug"/>? Compares letters only, either direction, so "boldr-1" matches
    /// boldrimpact.com, "leger2" matches leger360.com and "gravite" matches gravit-e.ca.
    /// Workday slugs (host/site) and Cornerstone slugs (tenant/site) use the tenant part.</summary>
    public static bool SlugMatchesDomain(string slug, string domain)
    {
        if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(domain)) return false;

        var tenant = slug.Split('/')[0].Split('.')[0];
        var labels = domain.ToLowerInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length == 0) return false;
        // Second-level label, skipping two-part suffixes like co.uk / com.au.
        var sld = labels.Length >= 3 && labels[^1].Length == 2 && labels[^2].Length <= 3
            ? labels[^3]
            : labels.Length >= 2 ? labels[^2] : labels[0];

        var a = LettersOnly(tenant);
        var b = LettersOnly(sld);
        if (a.Length < 3 || b.Length < 3) return false;
        return a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal);
    }

    /// <summary>Last two labels of a host — the ATS vendor's own domain (jobs.lever.co → lever.co,
    /// aritzia.wd3.myworkdayjobs.com → myworkdayjobs.com). Every ATS host we match is a plain
    /// .com/.co/.io, so no public-suffix handling is needed.</summary>
    internal static string RootDomain(string host)
    {
        var labels = host.Split('.');
        return labels.Length >= 2 ? $"{labels[^2]}.{labels[^1]}" : host;
    }

    private static string LettersOnly(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant())
            if (c is >= 'a' and <= 'z') sb.Append(c);
        return sb.ToString();
    }

    private static async Task<string?> FromWorkableAccountAsync(HttpClient http, string slug, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync($"https://apply.workable.com/api/v1/accounts/{Uri.EscapeDataString(slug)}", ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String) return null;
            var raw = url.GetString();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var extracted = DomainExtractor.Extract(raw.Contains("://", StringComparison.Ordinal) ? raw : $"https://{raw}");
            return extracted?.CanonicalDomain;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }
}
