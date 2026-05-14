using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using Jobnet.Models;

namespace Jobnet.Data.Repositories;

public sealed class JobRepository : IJobRepository
{
    private const string SelectAll = @"
        SELECT id, company_id, hash, hash_tier, title, url, location,
               remote_type, employment_type, level_id,
               description_snippet, salary_range, source, interest_level, notes,
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
                              description_snippet, salary_range, source, interest_level,
                              date_first_seen, date_last_seen, is_active)
            VALUES (@CompanyId, @HashKey, @HashTier, @Title, @Url, @Location,
                    @RemoteType, @EmploymentType, @LevelId,
                    @DescriptionSnippet, @SalaryRange, @Source, @InterestLevelText,
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
                Source = "fake-seed",
                InterestLevelText = ToDbText(job.InterestLevel),
                DateFirstSeenText = job.DateFirstSeen.ToUniversalTime().ToString("o"),
                DateLastSeenText = job.DateLastSeen.ToUniversalTime().ToString("o"),
            });

        if (job.AreaIds.Count > 0)
            _areas.SetAreasForJob(jobId, job.AreaIds);

        return jobId;
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
        SalaryRange = r.SalaryRange,
        InterestLevel = ParseInterest(r.InterestLevel),
        DateFirstSeen = DateTime.Parse(r.DateFirstSeen).ToUniversalTime(),
        DateLastSeen = DateTime.Parse(r.DateLastSeen).ToUniversalTime(),
        DateRemoved = string.IsNullOrEmpty(r.DateRemoved) ? null : DateTime.Parse(r.DateRemoved).ToUniversalTime(),
        IsActive = r.IsActive != 0,
    };

    private static string? ToDbText(InterestLevel level) => level switch
    {
        InterestLevel.Interesting    => "interesting",
        InterestLevel.NotInteresting => "not_interesting",
        _                            => null
    };

    private static InterestLevel ParseInterest(string? value) => value switch
    {
        "interesting"     => InterestLevel.Interesting,
        "not_interesting" => InterestLevel.NotInteresting,
        _                 => InterestLevel.Neutral
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
        public string? SalaryRange { get; set; }
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
