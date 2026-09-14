using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jobnet.Services.Discovery;

/// <summary>
/// Shared "same-host profile page -> real external company domain" resolution, used by anything
/// that discovers companies from a page that never links to the company's own site directly
/// (only to a same-host profile page) — originally lived only in
/// <see cref="CompanyDirectoryHarvester"/>, pulled out here so <c>TNetJobBoardIngestor</c> can
/// resolve/create companies with the exact same rules instead of a second, drifting copy.
/// </summary>
internal static class DomainResolution
{
    public static string CanonicalDomain(string website)
    {
        try
        {
            var u = website.Contains("://", StringComparison.Ordinal) ? website : $"https://{website}";
            var uri = new Uri(u);
            var host = uri.Host.ToLowerInvariant();
            if (host.StartsWith("www.", StringComparison.Ordinal)) host = host.Substring(4);
            return host;
        }
        catch
        {
            return "";
        }
    }

    private static readonly System.Text.RegularExpressions.Regex AnchorRe = new(
        @"<a[^>]+href=[""'](?<href>[^""']+)[""'][^>]*>(?<text>.*?)</a>",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.Singleline
        | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex TagRe =
        new(@"<[^>]+>", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex WsRe =
        new(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static List<(string Text, string Href)> ExtractAnchors(string html, string baseUrl, int max)
    {
        var list = new List<(string, string)>();
        foreach (System.Text.RegularExpressions.Match m in AnchorRe.Matches(html))
        {
            var rawText = TagRe.Replace(m.Groups["text"].Value, " ");
            var text = WsRe.Replace(System.Net.WebUtility.HtmlDecode(rawText), " ").Trim();
            var href = m.Groups["href"].Value.Trim();
            if (string.IsNullOrEmpty(href)) continue;
            if (href.StartsWith("#") || href.StartsWith("mailto:") || href.StartsWith("tel:")) continue;
            if (text.Length > 120) text = text.Substring(0, 120);
            if (!Uri.IsWellFormedUriString(href, UriKind.Absolute))
            {
                if (Uri.TryCreate(new Uri(baseUrl), href, out var combined)) href = combined.ToString();
            }
            list.Add((text, href));
            if (list.Count >= max) break;
        }
        return list;
    }

    /// <summary>Fetch a same-host profile page via plain HTTP (no JS) and extract the first
    /// plausible outbound company-website anchor — i.e. the first link whose host isn't
    /// <paramref name="sourceHost"/> and isn't filter-blocked.</summary>
    public static async Task<string?> TryResolveExternalDomainAsync(HttpClient http,
        Filters.FilterRuleProvider filters, string profileUrl, string sourceHost, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync(profileUrl, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var html = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrEmpty(html)) return null;
            var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? profileUrl;

            var anchors = ExtractAnchors(html, finalUrl, max: 80);
            foreach (var (_, href) in anchors)
            {
                var d = CanonicalDomain(href);
                if (string.IsNullOrEmpty(d)) continue;
                if (d.Equals(sourceHost, StringComparison.OrdinalIgnoreCase)) continue;
                if (filters.Current.IsHostBlocked(d, Models.FilterScope.Discovery)) continue;
                return d;
            }
        }
        catch { /* ignore — return null so caller skips this candidate */ }
        return null;
    }
}
