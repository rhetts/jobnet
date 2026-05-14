using System;
using System.Collections.Generic;
using Jobnet.Models;

namespace Jobnet.Data.Repositories;

public interface IJobRepository
{
    IReadOnlyList<Job> GetAll(bool includeRemoved = false);
    IReadOnlyList<Job> GetByCompany(int companyId, bool includeRemoved = false);
    Job? GetByHash(string hash);
    int Insert(Job job, int hashTier);

    /// <summary>Insert if hashKey is new; otherwise update date_last_seen and reactivate if needed.
    /// Returns (jobId, wasNew). hashTier is recorded only on first insert.</summary>
    (int Id, bool WasNew) Upsert(Job job, string hashKey, int hashTier);

    IReadOnlyList<int> GetActiveIdsForCompany(int companyId);
    void TouchLastSeen(int id, DateTime when);
    void MarkRemoved(int id, DateTime when);
    void Reactivate(int id, DateTime when);
    void SetInterestLevel(int id, InterestLevel level);
    Dictionary<int, int> GetActiveCountsByCompany();
}
