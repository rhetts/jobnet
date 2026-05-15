using System;
using System.Text.RegularExpressions;
using Jobnet.Models;

namespace Jobnet.Services.AtsAdapters;

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

    /// <summary>Returns the best-guess kind for a URL. Returns null if the URL is clearly not job-related
    /// (login, privacy, blog, etc.) so the caller can skip it.</summary>
    public static string? Classify(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var path = uri.AbsolutePath;
        var pathAndQuery = uri.PathAndQuery;

        var low = uri.AbsoluteUri.ToLowerInvariant();
        // Skip obvious non-job links so we don't pollute the cache.
        var skip = new[] {
            "/login", "/signin", "/sign-in", "/signup", "/register",
            "/privacy", "/cookie", "/terms", "/legal", "/security",
            "/contact", "/press", "/news", "/blog/", "/article/",
            "/about", "/team-members", "/leadership",
            "/investors", "/sustainability", "/diversity",
            "facebook.com", "twitter.com", "linkedin.com/company", "instagram.com",
            "youtube.com", "tiktok.com", "/feed", "/rss",
        };
        foreach (var s in skip) if (low.Contains(s)) return null;

        // Order matters: most-specific first.
        if (JobDetail.IsMatch(pathAndQuery))    return UrlKind.JobDetail;
        if (JobDetailAlt.IsMatch(pathAndQuery)) return UrlKind.JobDetail;
        if (DepartmentQuery.IsMatch(pathAndQuery)) return UrlKind.Department;
        if (DepartmentPath.IsMatch(pathAndQuery))  return UrlKind.Department;
        if (JobListPath.IsMatch(pathAndQuery))     return UrlKind.JobList;
        // Path contains careers/jobs but doesn't fit a more specific pattern
        if (low.Contains("/career") || low.Contains("/jobs")) return UrlKind.JobList;
        return null;
    }
}
