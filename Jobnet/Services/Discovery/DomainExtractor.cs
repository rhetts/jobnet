using System;

namespace Jobnet.Services.Discovery;

/// <summary>
/// Maps a URL to a "company key" suitable for dedup. Returns null only when the URL can't be
/// parsed — blocklisting moved to the filter_rule table (migration 053), so callers decide
/// what to reject by consulting FilterRuleSet.IsHostBlocked on the extracted host.
///
/// The 76-domain Skip set that used to live here was one of four divergent hardcoded lists;
/// it was the only one that handled subdomains, which is why ca.linkedin.com was rejected here
/// but sailed through CompanyDirectoryHarvester and AiCompetitorStrategy. That suffix semantics
/// now lives in FilterMatchType.Domain and applies everywhere.
/// </summary>
public static class DomainExtractor
{
    public static ExtractedCompany? Extract(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        if (host.Length == 0) return null;

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
}

public sealed class ExtractedCompany
{
    public required string CanonicalDomain { get; init; }
    public required string HostDomain { get; init; }
    public required string FullUrl { get; init; }
}
