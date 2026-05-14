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
}
