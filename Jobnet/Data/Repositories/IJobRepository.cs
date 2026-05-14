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
    void TouchLastSeen(int id, DateTime when);
    void MarkRemoved(int id, DateTime when);
    void Reactivate(int id, DateTime when);
    void SetInterestLevel(int id, InterestLevel level);
    Dictionary<int, int> GetActiveCountsByCompany();
}
