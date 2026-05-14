using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jobnet.Data.Repositories;
using Jobnet.Models;

namespace Jobnet.Services;

public sealed class SqliteJobDataService : IJobDataService
{
    private readonly ICompanyRepository _companies;
    private readonly IJobRepository _jobs;
    private readonly ILevelRepository _levels;
    private readonly IAreaRepository _areas;
    private readonly IConfigRepository _config;

    private readonly object _cacheLock = new();
    private Dictionary<int, int>? _levelScoreById;
    private Dictionary<int, int>? _areaScoreById;
    private DateTime _cacheTime = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public SqliteJobDataService(ICompanyRepository companies, IJobRepository jobs,
                                 ILevelRepository levels, IAreaRepository areas,
                                 IConfigRepository config)
    {
        _companies = companies;
        _jobs = jobs;
        _levels = levels;
        _areas = areas;
        _config = config;
    }

    public IReadOnlyList<Company> GetCompanies() => _companies.GetAll();

    public IReadOnlyList<Job> GetJobs() => _jobs.GetAll(includeRemoved: true);

    public int ScoreJob(Job job)
    {
        var (levelScores, areaScores) = GetScoreMaps();

        var levelScore = job.LevelId.HasValue && levelScores.TryGetValue(job.LevelId.Value, out var ls) ? ls : 0;
        var areaScore = job.AreaIds.Count == 0
            ? 0
            : job.AreaIds.Max(id => areaScores.TryGetValue(id, out var s) ? s : 0);

        var weightArea  = ParseDouble(_config.GetOrDefault("score_weight_area", "0.5"));
        var weightLevel = ParseDouble(_config.GetOrDefault("score_weight_level", "0.5"));
        var totalWeight = weightArea + weightLevel;
        if (totalWeight <= 0) return (areaScore + levelScore) / 2;
        return (int)Math.Round((areaScore * weightArea + levelScore * weightLevel) / totalWeight);
    }

    public void InvalidateScoreCache()
    {
        lock (_cacheLock)
        {
            _levelScoreById = null;
            _areaScoreById = null;
        }
    }

    private (Dictionary<int, int> Levels, Dictionary<int, int> Areas) GetScoreMaps()
    {
        lock (_cacheLock)
        {
            if (_levelScoreById is not null && _areaScoreById is not null &&
                DateTime.UtcNow - _cacheTime < CacheTtl)
            {
                return (_levelScoreById, _areaScoreById);
            }

            _levelScoreById = _levels.GetAll()
                .OrderBy(l => l.SortOrder)
                .ThenBy(l => l.Name)
                .Select((l, idx) => (l.Id, Score: Math.Max(20, 100 - idx * 20)))
                .ToDictionary(t => t.Id, t => t.Score);

            _areaScoreById = _areas.GetAll()
                .OrderBy(a => a.SortOrder)
                .ThenBy(a => a.Name)
                .Select((a, idx) => (a.Id, Score: Math.Max(20, 100 - idx * 20)))
                .ToDictionary(t => t.Id, t => t.Score);

            _cacheTime = DateTime.UtcNow;
            return (_levelScoreById, _areaScoreById);
        }
    }

    private static double ParseDouble(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0.5;
}
