using System;

namespace Jobnet.Models;

/// <summary>One row of the unified filter list. Replaces the hardcoded string arrays that used
/// to live in DomainExtractor, CompanyDirectoryHarvester, AiCompetitorStrategy and UrlClassifier.</summary>
public sealed class FilterRule
{
    public int Id { get; init; }
    public required string Subject { get; init; }
    public required string Action { get; init; }
    public required string MatchType { get; init; }
    public required string Pattern { get; init; }
    public string? Scope { get; init; }
    public string? Note { get; init; }
    public bool IsEnabled { get; set; }
    public int HitCount { get; set; }
    public DateTime? LastHit { get; set; }
}

/// <summary>What a rule is tested against.</summary>
public static class FilterSubject
{
    /// <summary>A canonical host, e.g. "ca.linkedin.com".</summary>
    public const string Host        = "host";
    /// <summary>A full absolute URL.</summary>
    public const string Url         = "url";
    // Reserved for later phases — the schema already accepts them.
    public const string JobTitle    = "job_title";
    public const string CompanyName = "company_name";
    public const string Location    = "location";
    public const string SearchToken = "search_token";
}

public static class FilterAction
{
    public const string Block    = "block";
    /// <summary>Wins over Block when both match. Used by the location rules in a later phase.</summary>
    public const string Allow    = "allow";
    public const string Greylist = "greylist";
    public const string Boost    = "boost";
}

public static class FilterMatchType
{
    /// <summary>.NET regex, case-insensitive.</summary>
    public const string Regex     = "regex";
    /// <summary>Plain case-insensitive Contains.</summary>
    public const string Substring = "substring";
    /// <summary>Exact host match, or any subdomain of it: "linkedin.com" matches "ca.linkedin.com".</summary>
    public const string Domain    = "domain";
    /// <summary>Whole-string equality, case-insensitive.</summary>
    public const string Exact     = "exact";
    /// <summary>Whole-word containment — the semantics the job-title greylist needs.</summary>
    public const string Word      = "word";
}

/// <summary>Narrows where a rule applies. Null means everywhere.</summary>
public static class FilterScope
{
    /// <summary>Fetching and classifying pages for an existing company.</summary>
    public const string Crawl     = "crawl";
    /// <summary>Deciding whether a domain becomes a company we track.</summary>
    public const string Discovery = "discovery";
}
