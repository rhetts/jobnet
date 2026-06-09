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
}
