using System;

namespace Jobnet.Data.Repositories;

/// <summary>Cache of job-board employer name -> company domain lookups (see migration 058).</summary>
public interface IJobBoardCompanyMapRepository
{
    /// <summary>The cached lookup for this employer, or null if it's never been resolved.
    /// A non-null entry with a null <see cref="JobBoardCompanyMapEntry.Domain"/> means the last
    /// lookup found no usable website.</summary>
    JobBoardCompanyMapEntry? Get(string source, string companyName);

    /// <summary>Insert or replace the lookup result. Pass a null domain for "no website found".</summary>
    void Save(string source, string companyName, string? domain);
}

public sealed record JobBoardCompanyMapEntry(string? Domain, DateTime ResolvedAtUtc);
