using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using Jobnet.Models;
using Jobnet.Services.Profile;

namespace Jobnet.Data.Repositories;

public sealed class JobRepository : IJobRepository
{
    private const string SelectAll = @"
        SELECT id, company_id, hash, hash_tier, title, url, location,
               remote_type, employment_type, level_id,
               description_snippet, summary, salary_range,
               salary_min AS SalaryMin, salary_max AS SalaryMax,
               salary_currency AS SalaryCurrency, salary_period AS SalaryPeriod,
               resume_match_score AS ResumeMatchScore,
               resume_match_reason AS ResumeMatchReason,
               applied_at AS AppliedAt,
               viewed_at AS ViewedAt,
               source, interest_level, notes,
               extraction_version, date_first_seen, date_last_seen, date_removed, is_active
        FROM jobs";

    private readonly IDbConnectionFactory _connections;
    private readonly IAreaRepository _areas;

    public JobRepository(IDbConnectionFactory connections, IAreaRepository areas)
    {
        _connections = connections;
        _areas = areas;
    }

    public IReadOnlyList<Job> GetAll(bool includeRemoved = false)
    {
        using var conn = _connections.Open();
        var sql = SelectAll + (includeRemoved ? "" : " WHERE is_active = 1") + " ORDER BY date_first_seen DESC";
        var rows = conn.Query<JobRow>(sql).ToList();
        return Hydrate(conn, rows);
    }

    public IReadOnlyList<Job> GetByCompany(int companyId, bool includeRemoved = false)
    {
        using var conn = _connections.Open();
        var sql = SelectAll + " WHERE company_id = @companyId" +
                  (includeRemoved ? "" : " AND is_active = 1") + " ORDER BY date_first_seen DESC";
        var rows = conn.Query<JobRow>(sql, new { companyId }).ToList();
        return Hydrate(conn, rows);
    }

    public Job? GetByHash(string hash)
    {
        using var conn = _connections.Open();
        var row = conn.QuerySingleOrDefault<JobRow>($"{SelectAll} WHERE hash = @hash", new { hash });
        if (row is null) return null;
        return Hydrate(conn, new[] { row }).First();
    }

    public int Insert(Job job, int hashTier)
    {
        using var conn = _connections.Open();
        var jobId = (int)conn.ExecuteScalar<long>(@"
            INSERT INTO jobs (company_id, hash, hash_tier, title, url, location,
                              remote_type, employment_type, level_id,
                              description_snippet, salary_range,
                              salary_min, salary_max, salary_currency, salary_period,
                              source, interest_level,
                              date_first_seen, date_last_seen, is_active)
            VALUES (@CompanyId, @HashKey, @HashTier, @Title, @Url, @Location,
                    @RemoteType, @EmploymentType, @LevelId,
                    @DescriptionSnippet, @SalaryRange,
                    @SalaryMin, @SalaryMax, @SalaryCurrency, @SalaryPeriod,
                    @Source, @InterestLevelText,
                    @DateFirstSeenText, @DateLastSeenText, 1);
            SELECT last_insert_rowid();",
            new
            {
                job.CompanyId,
                HashKey = ComputeHashKey(job, hashTier),
                HashTier = hashTier,
                job.Title,
                job.Url,
                job.Location,
                job.RemoteType,
                job.EmploymentType,
                job.LevelId,
                job.DescriptionSnippet,
                job.SalaryRange,
                job.SalaryMin,
                job.SalaryMax,
                job.SalaryCurrency,
                job.SalaryPeriod,
                Source = "fake-seed",
                InterestLevelText = ToDbText(job.InterestLevel),
                DateFirstSeenText = job.DateFirstSeen.ToUniversalTime().ToString("o"),
                DateLastSeenText = job.DateLastSeen.ToUniversalTime().ToString("o"),
            });

        if (job.AreaIds.Count > 0)
            _areas.SetAreasForJob(jobId, job.AreaIds);

        return jobId;
    }

    public (int Id, bool WasNew) Upsert(Job job, string hashKey, int hashTier)
    {
        using var conn = _connections.Open();
        var existingId = conn.QuerySingleOrDefault<int?>(
            "SELECT id FROM jobs WHERE hash = @hashKey", new { hashKey });

        if (existingId is null)
        {
            var newId = (int)conn.ExecuteScalar<long>(@"
                INSERT INTO jobs (company_id, hash, hash_tier, title, url, location,
                                  remote_type, employment_type, level_id,
                                  description_snippet, salary_range,
                                  salary_min, salary_max, salary_currency, salary_period,
                                  source, interest_level,
                                  date_first_seen, date_last_seen, is_active)
                VALUES (@CompanyId, @HashKey, @HashTier, @Title, @Url, @Location,
                        @RemoteType, @EmploymentType, @LevelId,
                        @DescriptionSnippet, @SalaryRange,
                        @SalaryMin, @SalaryMax, @SalaryCurrency, @SalaryPeriod,
                        @Source, @InterestLevelText,
                        @DateFirstSeenText, @DateLastSeenText, 1);
                SELECT last_insert_rowid();",
                new
                {
                    job.CompanyId, HashKey = hashKey, HashTier = hashTier,
                    job.Title, job.Url, job.Location, job.RemoteType, job.EmploymentType,
                    job.LevelId, job.DescriptionSnippet, job.SalaryRange,
                    job.SalaryMin, job.SalaryMax, job.SalaryCurrency, job.SalaryPeriod,
                    Source = "ats-refresh",
                    InterestLevelText = ToDbText(job.InterestLevel),
                    DateFirstSeenText = job.DateFirstSeen.ToUniversalTime().ToString("o"),
                    DateLastSeenText = job.DateLastSeen.ToUniversalTime().ToString("o"),
                });

            if (job.AreaIds.Count > 0) _areas.SetAreasForJob(newId, job.AreaIds);
            return (newId, true);
        }

        // Existing: bump last_seen, reactivate if removed, refresh salary if newly available
        conn.Execute(@"
            UPDATE jobs SET
                date_last_seen = @LastSeen,
                title = @Title,
                location = @Location,
                remote_type = COALESCE(@RemoteType, remote_type),
                employment_type = COALESCE(@EmploymentType, employment_type),
                level_id = COALESCE(@LevelId, level_id),
                description_snippet = COALESCE(@Description, description_snippet),
                salary_min      = COALESCE(@SalaryMin, salary_min),
                salary_max      = COALESCE(@SalaryMax, salary_max),
                salary_currency = COALESCE(@SalaryCurrency, salary_currency),
                salary_period   = COALESCE(@SalaryPeriod, salary_period),
                url = COALESCE(@Url, url),
                is_active = 1,
                date_removed = NULL
            WHERE id = @Id",
            new
            {
                Id = existingId.Value,
                LastSeen = job.DateLastSeen.ToUniversalTime().ToString("o"),
                job.Title, job.Location, job.RemoteType, job.EmploymentType,
                job.LevelId, Description = job.DescriptionSnippet, job.Url,
                job.SalaryMin, job.SalaryMax, job.SalaryCurrency, job.SalaryPeriod,
            });
        if (job.AreaIds.Count > 0) _areas.SetAreasForJob(existingId.Value, job.AreaIds);
        return (existingId.Value, false);
    }

    public IReadOnlyList<int> GetActiveIdsForCompany(int companyId)
    {
        using var conn = _connections.Open();
        return conn.Query<int>(
            "SELECT id FROM jobs WHERE company_id = @companyId AND is_active = 1",
            new { companyId }).ToList();
    }

    public void TouchLastSeen(int id, DateTime when)
    {
        using var conn = _connections.Open();
        conn.Execute("UPDATE jobs SET date_last_seen = @when WHERE id = @id",
            new { id, when = when.ToUniversalTime().ToString("o") });
    }

    public void MarkRemoved(int id, DateTime when)
    {
        using var conn = _connections.Open();
        conn.Execute("UPDATE jobs SET is_active = 0, date_removed = @when WHERE id = @id",
            new { id, when = when.ToUniversalTime().ToString("o") });
    }

    public void Reactivate(int id, DateTime when)
    {
        using var conn = _connections.Open();
        conn.Execute(@"
            UPDATE jobs SET is_active = 1, date_removed = NULL, date_last_seen = @when
            WHERE id = @id",
            new { id, when = when.ToUniversalTime().ToString("o") });
    }

    public void SetInterestLevel(int id, InterestLevel level)
    {
        using var conn = _connections.Open();
        conn.Execute("UPDATE jobs SET interest_level = @lvl WHERE id = @id",
            new { id, lvl = ToDbText(level) });
    }

    public Dictionary<int, int> GetActiveCountsByCompany()
    {
        using var conn = _connections.Open();
        return conn.Query<(int CompanyId, int Count)>(
            "SELECT company_id, COUNT(*) AS Count FROM jobs WHERE is_active = 1 GROUP BY company_id")
            .ToDictionary(r => r.CompanyId, r => r.Count);
    }

    public Dictionary<int, ChurnStat> GetChurnRate30dByCompany()
    {
        using var conn = _connections.Open();
        // Cohort = jobs whose date_first_seen is at least 30 days ago. The metric is the
        // proportion of that cohort now marked inactive. Companies with no cohort members
        // (added <30 days ago, or only ever had brand-new jobs) are simply absent from the
        // result dict — the ViewModel surfaces them as "n/a".
        var cutoff = DateTime.UtcNow.AddDays(-30).ToString("o");
        var rows = conn.Query<(int CompanyId, int Cohort, int Inactive)>(@"
            SELECT company_id AS CompanyId,
                   COUNT(*)   AS Cohort,
                   SUM(CASE WHEN is_active = 0 THEN 1 ELSE 0 END) AS Inactive
            FROM jobs
            WHERE date_first_seen <= @cutoff
            GROUP BY company_id", new { cutoff });
        return rows.ToDictionary(r => r.CompanyId,
            r => new ChurnStat(r.Cohort, r.Inactive,
                               r.Cohort > 0 ? 100.0 * r.Inactive / r.Cohort : 0));
    }

    public IReadOnlyList<(int Id, string? Location)> GetActiveLocations()
    {
        using var conn = _connections.Open();
        return conn.Query<(int Id, string? Location)>(
            "SELECT id, location FROM jobs WHERE is_active = 1").ToList();
    }

    public void SetSummary(int id, string summary, string model, DateTime generatedAt)
    {
        using var conn = _connections.Open();
        conn.Execute(@"
            UPDATE jobs SET summary = @summary,
                            summary_model = @model,
                            summary_generated_at = @when
            WHERE id = @id",
            new { id, summary, model, when = generatedAt.ToUniversalTime().ToString("o") });
    }

    public void SetLevel(int id, int? levelId)
    {
        using var conn = _connections.Open();
        conn.Execute("UPDATE jobs SET level_id = @levelId WHERE id = @id",
            new { id, levelId });
    }

    public void SetResumeMatch(int id, int score, string reason)
    {
        using var conn = _connections.Open();
        conn.Execute(@"
            UPDATE jobs SET resume_match_score = @score,
                            resume_match_reason = @reason,
                            resume_match_at = @when
            WHERE id = @id",
            new { id, score, reason, when = DateTime.UtcNow.ToString("o") });
    }

    public void ClearAllResumeMatches()
    {
        using var conn = _connections.Open();
        conn.Execute(@"
            UPDATE jobs SET resume_match_score = NULL,
                            resume_match_reason = NULL,
                            resume_match_at = NULL");
    }

    public void SetApplied(int id, bool isApplied)
    {
        using var conn = _connections.Open();
        conn.Execute("UPDATE jobs SET applied_at = @when WHERE id = @id",
            new { id, when = isApplied ? DateTime.UtcNow.ToString("o") : null });
    }

    public void SetViewed(int id, bool isViewed)
    {
        using var conn = _connections.Open();
        conn.Execute("UPDATE jobs SET viewed_at = @when WHERE id = @id",
            new { id, when = isViewed ? DateTime.UtcNow.ToString("o") : null });
    }

    public IReadOnlyList<Job> GetJobsNeedingSummary(int max = 200)
    {
        using var conn = _connections.Open();
        // Need either a description we can summarize, or a URL we can fetch.
        var sql = SelectAll + @"
            WHERE is_active = 1
              AND (summary IS NULL OR summary = '')
              AND ((description_snippet IS NOT NULL AND LENGTH(description_snippet) > 40)
                   OR (url IS NOT NULL AND LENGTH(url) > 0))
            ORDER BY date_first_seen DESC
            LIMIT @max";
        var rows = conn.Query<JobRow>(sql, new { max }).ToList();
        return Hydrate(conn, rows);
    }

    private static IReadOnlyList<Job> Hydrate(System.Data.IDbConnection conn, IEnumerable<JobRow> rows)
    {
        var jobs = rows.Select(MapToJob).ToList();
        if (jobs.Count == 0) return jobs;

        var ids = jobs.Select(j => j.Id).ToArray();
        var areaLookup = conn.Query<(int JobId, int AreaId)>(
            "SELECT job_id, area_id FROM job_areas WHERE job_id IN @ids", new { ids })
            .GroupBy(r => r.JobId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<int>)g.Select(r => r.AreaId).ToList());

        foreach (var job in jobs)
            job.AreaIds = areaLookup.TryGetValue(job.Id, out var areas) ? areas : Array.Empty<int>();

        return jobs;
    }

    private static string ComputeHashKey(Job job, int hashTier)
    {
        var raw = $"{hashTier}|{job.CompanyId}|{job.Title}|{job.Location}|{job.Url}";
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static Job MapToJob(JobRow r) => new()
    {
        Id = r.Id,
        CompanyId = r.CompanyId,
        Title = r.Title,
        Url = r.Url,
        Location = r.Location,
        RemoteType = r.RemoteType,
        EmploymentType = r.EmploymentType,
        LevelId = r.LevelId,
        DescriptionSnippet = r.DescriptionSnippet,
        Summary = r.Summary,
        SalaryRange = r.SalaryRange,
        SalaryMin = r.SalaryMin,
        SalaryMax = r.SalaryMax,
        SalaryCurrency = r.SalaryCurrency,
        SalaryPeriod = r.SalaryPeriod,
        ResumeMatchScore = r.ResumeMatchScore,
        ResumeMatchReason = r.ResumeMatchReason,
        DateApplied = string.IsNullOrEmpty(r.AppliedAt) ? null : DateTime.Parse(r.AppliedAt).ToUniversalTime(),
        DateViewed = string.IsNullOrEmpty(r.ViewedAt) ? null : DateTime.Parse(r.ViewedAt).ToUniversalTime(),
        InterestLevel = ParseInterest(r.InterestLevel),
        DateFirstSeen = DateTime.Parse(r.DateFirstSeen).ToUniversalTime(),
        DateLastSeen = DateTime.Parse(r.DateLastSeen).ToUniversalTime(),
        DateRemoved = string.IsNullOrEmpty(r.DateRemoved) ? null : DateTime.Parse(r.DateRemoved).ToUniversalTime(),
        IsActive = r.IsActive != 0,
    };

    public int ApplyGreylist(string? rawGreylist)
    {
        var tokens = GreylistMatcher.Parse(rawGreylist);
        if (tokens.Count == 0) return 0;

        using var conn = _connections.Open();
        // Only sweep Neutral jobs — never override a user's explicit Approve or Downvote.
        // Neutral in this codebase is stored as NULL (ToDbText returns null for Neutral).
        var rows = conn.Query<(int Id, string Title, string? Summary, string? DescriptionSnippet, string? CompanyName)>(@"
            SELECT j.id   AS Id,
                   j.title AS Title,
                   j.summary AS Summary,
                   j.description_snippet AS DescriptionSnippet,
                   c.name AS CompanyName
            FROM jobs j
            LEFT JOIN companies c ON c.id = j.company_id
            WHERE j.is_active = 1
              AND j.interest_level IS NULL").ToList();

        var updated = 0;
        foreach (var r in rows)
        {
            if (GreylistMatcher.MatchesAny(tokens, r.Title, r.Summary, r.DescriptionSnippet, r.CompanyName))
            {
                SetInterestLevel(r.Id, InterestLevel.NotInteresting);
                updated++;
            }
        }
        return updated;
    }

    // DB text is constrained by a CHECK from migration 001 to ('interesting','not_interesting').
    // The C# enum value was renamed to Approved but DB still stores 'interesting' so we don't
    // have to rebuild the table. Parse maps both spellings just in case a future migration
    // relaxes the constraint and the DB ever contains "approved".
    private static string? ToDbText(InterestLevel level) => level switch
    {
        InterestLevel.Approved       => "interesting",
        InterestLevel.NotInteresting => "not_interesting",
        _                            => null
    };

    private static InterestLevel ParseInterest(string? value) => value switch
    {
        "approved" or "interesting" => InterestLevel.Approved,
        "not_interesting"           => InterestLevel.NotInteresting,
        _                           => InterestLevel.Neutral
    };

    private sealed class JobRow
    {
        public int Id { get; set; }
        public int CompanyId { get; set; }
        public string Hash { get; set; } = string.Empty;
        public int HashTier { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Url { get; set; }
        public string? Location { get; set; }
        public string? RemoteType { get; set; }
        public string? EmploymentType { get; set; }
        public int? LevelId { get; set; }
        public string? DescriptionSnippet { get; set; }
        public string? Summary { get; set; }
        public string? SalaryRange { get; set; }
        public int? SalaryMin { get; set; }
        public int? SalaryMax { get; set; }
        public string? SalaryCurrency { get; set; }
        public string? SalaryPeriod { get; set; }
        public int?    ResumeMatchScore { get; set; }
        public string? ResumeMatchReason { get; set; }
        public string? AppliedAt { get; set; }
        public string? ViewedAt { get; set; }
        public string? Source { get; set; }
        public string? InterestLevel { get; set; }
        public string? Notes { get; set; }
        public string? ExtractionVersion { get; set; }
        public string DateFirstSeen { get; set; } = string.Empty;
        public string DateLastSeen { get; set; } = string.Empty;
        public string? DateRemoved { get; set; }
        public int IsActive { get; set; }
    }
}
