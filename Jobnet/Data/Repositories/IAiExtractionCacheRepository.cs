namespace Jobnet.Data.Repositories;

/// <summary>Cache of parsed AI-extracted job lists, keyed by URL. The content hash detects
/// page changes so we only return cached results when the input we'd send to the AI is
/// byte-identical to the input that produced the cached output.</summary>
public interface IAiExtractionCacheRepository
{
    /// <summary>Returns the cached jobs_json if a row exists for <paramref name="url"/>, its
    /// <paramref name="contentHash"/> matches, and the row is younger than ttlHours. Increments
    /// hit_count on a hit. Returns null otherwise.</summary>
    string? GetIfFresh(string url, string contentHash, int ttlHours);

    /// <summary>Returns the cached jobs_json for <paramref name="url"/> if it was cached within
    /// the last <paramref name="withinHours"/>, ignoring content_hash. Lets the caller skip the
    /// whole Playwright + AI roundtrip on URLs we just scraped — the bet is the page hasn't
    /// meaningfully changed within a short window. Increments hit_count when used. Set
    /// withinHours = 0 to disable. Returns null when no fresh row exists.</summary>
    string? GetByUrlIfRecent(string url, int withinHours);

    /// <summary>Insert or replace the cache row for <paramref name="url"/>.</summary>
    void Put(string url, string contentHash, string jobsJson);
}
