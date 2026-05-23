using System;

namespace Jobnet.Data.Repositories;

public interface IDirectoryCrawlRepository
{
    /// <summary>Most recent successful crawl time for a URL, or null if never crawled.</summary>
    DateTime? GetLastCrawlUtc(string url);

    /// <summary>Insert a crawl record. duration_ms may be null if not measured.</summary>
    void Record(string url, DateTime fetchedAtUtc, int? durationMs,
                 int candidatesFound, int candidatesAdded, bool success, string? error);
}
