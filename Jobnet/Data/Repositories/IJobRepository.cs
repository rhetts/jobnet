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

    /// <summary>Return id+location for every active job. Used by location-prune passes.</summary>
    IReadOnlyList<(int Id, string? Location)> GetActiveLocations();

    /// <summary>Persist an AI-generated job summary paragraph.</summary>
    void SetSummary(int id, string summary, string model, DateTime generatedAt);

    /// <summary>Update a job's classified level (without touching other fields).</summary>
    void SetLevel(int id, int? levelId);

    /// <summary>Persist a resume match score (0-100) and short rationale for one job.</summary>
    void SetResumeMatch(int id, int score, string reason);

    /// <summary>Wipe all resume_match_* fields. Called when the user uploads a new resume.</summary>
    void ClearAllResumeMatches();

    /// <summary>Mark a job as applied (sets applied_at = now) or un-applied (sets applied_at = null).</summary>
    void SetApplied(int id, bool isApplied);

    /// <summary>Mark a job as viewed (sets viewed_at = now) or un-viewed (sets viewed_at = null).</summary>
    void SetViewed(int id, bool isViewed);

    /// <summary>Return active jobs that have no summary yet (and have some text to summarize).</summary>
    IReadOnlyList<Job> GetJobsNeedingSummary(int max = 200);

    /// <summary>Sweep every Neutral active job and set InterestLevel to NotInteresting on any whose
    /// title / summary / description snippet / company name contains a greylist keyword
    /// (whole-word, case-insensitive). Approved or already-downvoted jobs are NOT touched —
    /// the greylist never overrides a user's explicit choice. Returns the count of jobs
    /// newly downvoted.</summary>
    int ApplyGreylist(string? rawGreylist);
}
