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

    /// <summary>Persist the per-refresh health rollup: last jobs count, consecutive_failures
    /// (incremented on no-jobs OR failure, reset to 0 on ≥1 job), last_success_at (stamped only
    /// when jobsCount &gt; 0). Call once per company per RefreshOneAsync.</summary>
    void RecordRefreshResult(int id, int jobsCount, bool hadFailure);

    /// <summary>Clear <c>ats_slug</c> so detect-ats will re-run on the next refresh. Used by the
    /// auto-clear-stale-slug rule when consecutive_failures hits the threshold for a company
    /// with a 4xx-returning slug. Appends a reason to <c>notes</c> so the user can see why.</summary>
    void ClearAtsSlug(int id, string reason);
}
