using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Jobnet.Data;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Jobnet.Services.Classification;

namespace Jobnet.Services.AtsAdapters;

public sealed class JobRefresher : IJobRefresher
{
    private readonly Dictionary<string, IAtsJobSource> _sources;
    private readonly ICompanyRepository _companies;
    private readonly IJobRepository _jobs;
    private readonly IAreaRepository _areas;
    private readonly IJobClassifier _classifier;
    private readonly IDbConnectionFactory _connections;

    public JobRefresher(IEnumerable<IAtsJobSource> sources, ICompanyRepository companies,
                         IJobRepository jobs, IAreaRepository areas,
                         IJobClassifier classifier, IDbConnectionFactory connections)
    {
        _sources = sources.ToDictionary(s => s.AtsType, StringComparer.OrdinalIgnoreCase);
        _companies = companies;
        _jobs = jobs;
        _areas = areas;
        _classifier = classifier;
        _connections = connections;
    }

    public async Task<JobRefreshReport> RefreshAsync(Company company, CancellationToken ct = default)
    {
        var scanId = StartScanLog(company.Domain);
        var errors = new List<string>();
        var added = 0; var updated = 0; var removed = 0;
        var skipped = 0; var failed = 0;
        var processed = 0;

        try
        {
            (added, updated, removed) = await RefreshOneAsync(company, errors, ct);
            processed = 1;
        }
        catch (NoAdapterException)
        {
            skipped = 1;
        }
        catch (Exception ex)
        {
            errors.Add($"[{company.Domain}] {ex.Message}");
            failed = 1;
        }

        FinishScanLog(scanId, processed, added, removed, errors);
        return new JobRefreshReport
        {
            CompaniesProcessed = processed,
            CompaniesSkippedNoAts = skipped,
            CompaniesFailed = failed,
            JobsAdded = added, JobsUpdated = updated, JobsRemoved = removed,
            Errors = errors,
        };
    }

    public async Task<JobRefreshReport> RefreshAllAsync(CancellationToken ct = default)
    {
        var scanId = StartScanLog("global");
        var errors = new List<string>();
        var added = 0; var updated = 0; var removed = 0;
        var skipped = 0; var failed = 0; var processed = 0;

        var all = _companies.GetAll();
        foreach (var c in all)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(c.AtsType) || string.IsNullOrEmpty(c.AtsSlug))
            {
                skipped++;
                continue;
            }
            try
            {
                var (a, u, r) = await RefreshOneAsync(c, errors, ct);
                added += a; updated += u; removed += r;
                processed++;
            }
            catch (NoAdapterException)
            {
                skipped++;
            }
            catch (Exception ex)
            {
                errors.Add($"[{c.Domain}] {ex.Message}");
                failed++;
            }
        }

        FinishScanLog(scanId, processed, added, removed, errors);
        return new JobRefreshReport
        {
            CompaniesProcessed = processed,
            CompaniesSkippedNoAts = skipped,
            CompaniesFailed = failed,
            JobsAdded = added, JobsUpdated = updated, JobsRemoved = removed,
            Errors = errors,
        };
    }

    private async Task<(int Added, int Updated, int Removed)> RefreshOneAsync(Company company, List<string> errors, CancellationToken ct)
    {
        IAtsJobSource source;
        string sourceArg;

        if (!string.IsNullOrEmpty(company.AtsType) && !string.IsNullOrEmpty(company.AtsSlug)
            && _sources.TryGetValue(company.AtsType, out var native))
        {
            source = native;
            sourceArg = company.AtsSlug;
        }
        else if (_sources.TryGetValue("ai_extract", out var ai))
        {
            // Fallback: Playwright + AI extraction on the careers page (or homepage if nothing else known)
            source = ai;
            sourceArg = company.CareersUrl
                     ?? company.WebsiteUrl
                     ?? $"https://{company.Domain}/careers";
        }
        else
        {
            throw new NoAdapterException();
        }

        var raw = await source.FetchAsync(sourceArg, ct);
        var added = 0; var updated = 0;
        var seenJobIds = new HashSet<int>();

        foreach (var r in raw)
        {
            ct.ThrowIfCancellationRequested();
            // For native ATS, the type is stable on company.AtsType. For AI fallback, use the source's type.
            var hashKey = $"{source.AtsType}:{company.Id}:{r.NativeId}";
            var classified = _classifier.Classify(r.Title, r.Department);

            var job = new Job
            {
                Id = 0,
                CompanyId = company.Id,
                Title = r.Title,
                Url = r.Url,
                Location = r.Location,
                RemoteType = NormalizeRemote(r.RemoteType),
                EmploymentType = NormalizeEmployment(r.EmploymentType),
                LevelId = classified.LevelId,
                AreaIds = classified.Areas.Select(a => a.Id).ToList(),
                DescriptionSnippet = r.DescriptionSnippet,
                SalaryRange = r.SalaryRange,
                InterestLevel = InterestLevel.Neutral,
                DateFirstSeen = DateTime.UtcNow,
                DateLastSeen = DateTime.UtcNow,
                IsActive = true,
            };

            var (id, wasNew) = _jobs.Upsert(job, hashKey, hashTier: 1);
            seenJobIds.Add(id);
            if (wasNew) added++; else updated++;
        }

        // Mark previously-active jobs that no longer appear as removed
        var existingActiveIds = _jobs.GetActiveIdsForCompany(company.Id);
        var removedCount = 0;
        foreach (var id in existingActiveIds)
        {
            if (!seenJobIds.Contains(id))
            {
                _jobs.MarkRemoved(id, DateTime.UtcNow);
                removedCount++;
            }
        }

        _companies.SetLastScan(company.Id, DateTime.UtcNow);
        return (added, updated, removedCount);
    }

    private long StartScanLog(string scope)
    {
        using var conn = _connections.Open();
        return conn.ExecuteScalar<long>(@"
            INSERT INTO scan_log (scan_time, scan_type, scope, status)
            VALUES (@now, 'refresh_jobs', @scope, 'running');
            SELECT last_insert_rowid();",
            new { now = DateTime.UtcNow.ToString("o"), scope });
    }

    private void FinishScanLog(long id, int companiesHit, int added, int removed, IReadOnlyList<string> errors)
    {
        using var conn = _connections.Open();
        conn.Execute(@"
            UPDATE scan_log SET status = @status, companies_hit = @companiesHit,
                                jobs_added = @added, jobs_removed = @removed,
                                errors = @errors
            WHERE id = @id",
            new
            {
                id,
                status = errors.Count == 0 ? "completed" : "partial",
                companiesHit, added, removed,
                errors = errors.Count == 0 ? null : JsonSerializer.Serialize(errors)
            });
    }

    private sealed class NoAdapterException : Exception { }

    /// <summary>Map various ATS string forms to the DB CHECK enum values.</summary>
    private static string NormalizeRemote(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "unknown";
        var v = value.ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
        if (v.Contains("remote")) return "remote";
        if (v.Contains("hybrid")) return "hybrid";
        if (v.Contains("onsite") || v.Contains("inoffice") || v.Contains("inperson")) return "on-site";
        return "unknown";
    }

    private static string NormalizeEmployment(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "unknown";
        var v = value.ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
        if (v.Contains("fulltime")) return "full-time";
        if (v.Contains("parttime")) return "part-time";
        if (v.Contains("contract") || v.Contains("freelance") || v.Contains("temp")) return "contract";
        return "unknown";
    }
}
