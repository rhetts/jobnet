using System;
using System.Text.RegularExpressions;
using Jobnet.Models;
using Jobnet.Services.Filters;

namespace Jobnet.Services.JobSources;

/// <summary>Classifies a URL by purpose based on path and query patterns commonly seen on careers pages.</summary>
internal static class UrlClassifier
{
    private static readonly Regex JobDetail = new(
        @"/(jobs?|positions?|career[s]?|openings?|roles?)/[a-z0-9_-]*\d+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex JobDetailAlt = new(
        @"/(jobs?|positions?|openings?|apply)/[a-z0-9][a-z0-9_-]{6,}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DepartmentQuery = new(
        @"[?&](department|team|category|division|function|group)=",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DepartmentPath = new(
        @"/(department|team|category|division|function)s?/[a-z0-9_-]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex JobListPath = new(
        @"^/(jobs?|careers?|openings?|positions?|roles?)/?(?:\?.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Returns the best-guess kind for a URL. Returns null if the URL is blocked, or is
    /// clearly not job-related, so the caller can skip it.
    ///
    /// <paramref name="filters"/> is consulted FIRST, before any positive pattern. That ordering
    /// is the whole point: the old inline skip list ran last, so DepartmentPath claimed
    /// /category/all-news/page/5/ as a department before anything could veto it — which is how
    /// Blue Ant Media accumulated 35 WordPress news archives in company_urls. It also means a
    /// host rule for linkedin.com now stops linkedin.com/jobs/view/123, which the old
    /// "/jobs" positive check happily classified as a job board.</summary>
    public static string? Classify(string url, FilterRuleSet? filters = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        if (filters is not null && filters.IsUrlBlocked(url, FilterScope.Crawl)) return null;

        var pathAndQuery = uri.PathAndQuery;
        var low = uri.AbsoluteUri.ToLowerInvariant();

        if (JobDetail.IsMatch(pathAndQuery))       return UrlKind.JobDetail;
        if (JobDetailAlt.IsMatch(pathAndQuery))    return UrlKind.JobDetail;
        if (DepartmentQuery.IsMatch(pathAndQuery)) return UrlKind.Department;
        if (DepartmentPath.IsMatch(pathAndQuery))  return UrlKind.Department;
        if (JobListPath.IsMatch(pathAndQuery))     return UrlKind.JobList;
        if (low.Contains("/career") || low.Contains("/jobs")) return UrlKind.JobList;

        // No positive job signal. The inline skip list that used to sit here was dead code —
        // both it and the fall-through returned null — so it's gone. Its useful entries
        // (auth pages, policy pages, feeds) are now url-subject rows in filter_rule, where
        // they actually run.
        return null;
    }
}
