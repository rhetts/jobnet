using System;
using System.Collections.Generic;

namespace Jobnet.Services.Discovery;

/// <summary>
/// Maps a URL to a "company key" suitable for dedup. Returns null when the URL is from a
/// domain we want to skip (aggregators, social, news/wiki).
/// </summary>
public static class DomainExtractor
{
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        // Job aggregators — we want companies, not job listings
        "linkedin.com", "indeed.com", "indeed.ca", "glassdoor.com", "glassdoor.ca",
        "monster.com", "monster.ca", "workopolis.com", "ziprecruiter.com",
        "wellfound.com", "angel.co", "simplyhired.com", "simplyhired.ca",
        // Social
        "facebook.com", "twitter.com", "x.com", "instagram.com",
        "youtube.com", "tiktok.com", "reddit.com",
        // Reference / aggregator profiles
        "wikipedia.org", "crunchbase.com", "quora.com", "medium.com",
        "github.com", "gitlab.com", "investopedia.com",
        // News
        "businessinsider.com", "techcrunch.com", "forbes.com", "bloomberg.com",
        "betakit.com", "biv.com", "globalnews.ca", "cbc.ca",
        // B2B review / firm-directory sites — they list services, not the actual companies we want
        "clutch.co", "goodfirms.co", "themanifest.com", "designrush.com", "sortlist.com",
        "g2.com", "capterra.com", "trustpilot.com",
        // Startup directories
        "topstartups.io", "startus-insights.com", "startups-list.com", "openvc.app",
        "getlatka.com", "builtin.com", "builtinvancouver.org",
        // Industry-specific directories
        "gamecompanies.com", "canadiangamedevs.com", "thisgamestudio.com", "cloudtango.net",
        "fintechcadence.com", "ainbc.ai",
        // Lead-gen / scrapers (not real companies)
        "aeroleads.com", "zoominfo.com", "rocketreach.co",
        // Canadian gov / education / regulatory
        "britishcolumbia.ca", "canada.ca", "gc.ca", "bcit.ca", "vcc.ca", "bcsc.bc.ca",
        "ic.gc.ca", "innovation.ca",
        // Generic
        "google.com", "bing.com", "duckduckgo.com",
    };

    public static ExtractedCompany? Extract(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];

        if (IsSkipped(host)) return null;

        // Strip common careers/jobs subdomains for canonical domain
        var canonical = host;
        if (canonical.StartsWith("careers.")) canonical = canonical[8..];
        else if (canonical.StartsWith("jobs.")) canonical = canonical[5..];
        else if (canonical.StartsWith("hire.")) canonical = canonical[5..];

        return new ExtractedCompany
        {
            CanonicalDomain = canonical,
            HostDomain = host,
            FullUrl = url,
        };
    }

    /// <summary>True if `host` exactly matches any skip entry or is a sub-host of one.
    /// Lets us skip `bcsc.bc.ca` (multi-label provincial domain) without false-matching `*.bc.ca` companies.</summary>
    private static bool IsSkipped(string host)
    {
        if (Skip.Contains(host)) return true;
        foreach (var entry in Skip)
        {
            // Match suffix `.entry` (so `careers.linkedin.com` skips because of `linkedin.com`)
            if (host.EndsWith("." + entry, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

public sealed class ExtractedCompany
{
    public required string CanonicalDomain { get; init; }
    public required string HostDomain { get; init; }
    public required string FullUrl { get; init; }
}
