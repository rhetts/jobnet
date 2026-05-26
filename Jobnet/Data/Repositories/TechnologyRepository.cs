using System.Collections.Generic;
using System.Linq;
using Dapper;
using Jobnet.Models;

namespace Jobnet.Data.Repositories;

public sealed class TechnologyRepository : ITechnologyRepository
{
    private readonly IDbConnectionFactory _connections;

    public TechnologyRepository(IDbConnectionFactory connections)
    {
        _connections = connections;
    }

    public IReadOnlyList<Technology> GetAll()
    {
        using var conn = _connections.Open();
        return conn.Query<Row>(
            "SELECT id, slug, name, kind FROM technologies ORDER BY kind, name")
            .Select(r => new Technology { Id = r.id, Slug = r.slug, Name = r.name, Kind = r.kind })
            .ToList();
    }

    public IReadOnlyDictionary<string, int> GetAliasMap()
    {
        using var conn = _connections.Open();
        return conn.Query<(string alias, int tech_id)>(
            "SELECT alias, technology_id FROM technology_aliases")
            .ToDictionary(r => r.alias, r => r.tech_id, System.StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<int> GetForJob(int jobId)
    {
        using var conn = _connections.Open();
        return conn.Query<int>(
            "SELECT technology_id FROM job_technologies WHERE job_id = @jobId ORDER BY technology_id",
            new { jobId }).ToList();
    }

    public Dictionary<int, List<int>> GetAllJobTechnologies()
    {
        using var conn = _connections.Open();
        var result = new Dictionary<int, List<int>>();
        foreach (var (jobId, techId) in conn.Query<(int jobId, int techId)>(
            "SELECT job_id, technology_id FROM job_technologies"))
        {
            if (!result.TryGetValue(jobId, out var list))
            {
                list = new List<int>();
                result[jobId] = list;
            }
            list.Add(techId);
        }
        return result;
    }

    public void SetForJob(int jobId, IReadOnlyCollection<int> technologyIds)
    {
        using var conn = _connections.Open();
        using var tx = conn.BeginTransaction();
        // Replace, not merge — matcher is idempotent on a (job, text) input. Re-running the
        // backfill or refresh must produce the same end state, never a duplicate row.
        conn.Execute("DELETE FROM job_technologies WHERE job_id = @jobId", new { jobId }, tx);
        if (technologyIds.Count > 0)
        {
            conn.Execute(
                "INSERT INTO job_technologies (job_id, technology_id) VALUES (@jobId, @techId)",
                technologyIds.Distinct().Select(id => new { jobId, techId = id }),
                tx);
        }
        tx.Commit();
    }

    public Dictionary<int, int> GetActiveCountsByTechnology()
    {
        using var conn = _connections.Open();
        return conn.Query<(int tech_id, int n)>(@"
            SELECT jt.technology_id, COUNT(*) AS n
            FROM job_technologies jt
            JOIN jobs j ON j.id = jt.job_id
            WHERE j.is_active = 1
            GROUP BY jt.technology_id")
            .ToDictionary(r => r.tech_id, r => r.n);
    }

    private sealed class Row
    {
        public int id { get; set; }
        public string slug { get; set; } = "";
        public string name { get; set; } = "";
        public string kind { get; set; } = "";
    }
}
