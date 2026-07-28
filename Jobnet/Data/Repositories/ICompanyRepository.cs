using System;
using System.Collections.Generic;
using Jobnet.Models;

namespace Jobnet.Data.Repositories;

public interface ICompanyRepository
{
    IReadOnlyList<Company> GetAll();
    Company? GetById(int id);
    Company? GetByDomain(string domain);
    int Insert(Company company);
    void Update(Company company);
    void SetInterestLevel(int id, InterestLevel level);
    void SetLastScan(int id, DateTime when);

    /// <summary>Toggle the <c>is_active</c> flag. Inactive companies are skipped by the
    /// refresh-all loop but remain in the DB so their historical jobs survive.</summary>
    void SetActive(int id, bool active);

    void SetAtsInfo(int id, string? atsType, string? atsSlug, string? careersUrl);
    Jobnet.Models.CompanyProfile? GetProfile(int companyId);

    /// <summary>Persist a freshly-derived AI selector profile. Clears any prior error/drift state
    /// because deriving a new profile is by definition a fresh start.</summary>
    void SetParserStrategy(int id, string profileJson, DateTime derivedAt);

    /// <summary>Record the outcome of running the selector profile during a refresh. Pass null
    /// errorMessage on success; on drift/error it gets surfaced in the Parser Report screen.</summary>
    void SetParserStrategyResult(int id, string result, DateTime when, string? errorMessage);

    /// <summary>Clear the cached selector profile. Called on drift (so the next refresh re-derives)
    /// or from the report screen as "force re-derive".</summary>
    void ClearParserStrategy(int id);

    void SetParserStrategyDisabled(int id, bool disabled);

    /// <summary>Record which hand-written IHtmlPatternParser most recently produced jobs for the
    /// company. Passing null clears the attribution (e.g. when the company falls back to AI
    /// extraction after previously matching a parser).</summary>
    void SetLastCompanyParser(int id, string? parserName);

    /// <summary>Active company ids whose <c>profile_summary</c> hasn't been generated yet.
    /// Used by the queue-backfill CLI to seed the CompanyProfileWorker.</summary>
    System.Collections.Generic.IReadOnlyList<int> GetActiveIdsMissingProfile();

    /// <summary>Persist the per-refresh health rollup: last jobs count, consecutive_failures
    /// (incremented on no-jobs OR failure, reset to 0 on ≥1 job), last_success_at (stamped only
    /// when jobsCount &gt; 0). Call once per company per RefreshOneAsync.</summary>
    void RecordRefreshResult(int id, int jobsCount, bool hadFailure);

    /// <summary>Clear <c>ats_slug</c> so detect-ats will re-run on the next refresh. Used by the
    /// auto-clear-stale-slug rule when consecutive_failures hits the threshold for a company
    /// with a 4xx-returning slug. Appends a reason to <c>notes</c> so the user can see why.</summary>
    void ClearAtsSlug(int id, string reason);

    /// <summary>Flip a company's blacklist flag. When true, the refresh loop skips it and the
    /// main job view hides every job posted by it. Set to false to bring it back.</summary>
    void SetBlacklisted(int id, bool blacklisted);

    /// <summary>Returns one row per active company with the fields the Sources screen needs:
    /// the size category (already bucketed), the website URL, careers URL, and the seed name(s)
    /// it was discovered from. Built in one query so the screen doesn't N+1.</summary>
    IReadOnlyList<CompanySourceRow> GetCompanySourceRows();

    /// <summary>Re-derive <c>size_category</c> from every row's existing <c>profile_size_hint</c>
    /// and persist. Returns the count of rows updated. Used as a one-off backfill after migration
    /// 051 so existing companies get their bucket without waiting for the next profile refresh.</summary>
    int BackfillSizeCategories(System.Func<string?, string?> classifier);
}

/// <summary>One row shown on the Sources screen. <see cref="SizeCategory"/> is one of
/// <c>startup/growth/mid_size/large</c> or null when unknown. <see cref="SourceNames"/> is a
/// semicolon-joined list of discovery seed names (e.g. "Relay Ventures portfolio; Sequoia").</summary>
public sealed class CompanySourceRow
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public string? Domain { get; init; }
    public string? WebsiteUrl { get; init; }
    public string? CareersUrl { get; init; }
    public string? City { get; init; }
    public string? SizeCategory { get; init; }
    public string? SourceNames { get; init; }
    public bool IsActive { get; init; }
}
