using System;
using System.Collections.Generic;

namespace Jobnet.Data.Repositories;

public interface IDirectoryCrawlRepository
{
    /// <summary>Most recent successful crawl time for a URL, or null if never crawled.</summary>
    DateTime? GetLastCrawlUtc(string url);

    /// <summary>Insert a crawl record. duration_ms may be null if not measured.</summary>
    void Record(string url, DateTime fetchedAtUtc, int? durationMs,
                 int candidatesFound, int candidatesAdded, bool success, string? error);

    /// <summary>All-time candidate/added totals grouped by exact crawled URL (paginated URLs like
    /// "...?page=2" stay separate rows — callers that want per-source totals across pages should
    /// group these by a normalized base URL). Used by the Monitor window's Sources tab.</summary>
    IReadOnlyList<DirectoryCrawlTotal> GetTotalsByUrl();
}

/// <summary>All-time crawl totals for one exact URL. See <see cref="IDirectoryCrawlRepository.GetTotalsByUrl"/>.</summary>
public sealed class DirectoryCrawlTotal
{
    public string Url { get; set; } = "";
    public int CandidatesFound { get; set; }
    public int CandidatesAdded { get; set; }
    public int Runs { get; set; }
}
