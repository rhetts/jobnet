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
    private readonly AiExtractedJobSource _aiSource;
    private readonly ICompanyRepository _companies;
    private readonly ICompanyUrlsRepository _urls;
    private readonly IJobRepository _jobs;
    private readonly IAreaRepository _areas;
    private readonly IJobClassifier _classifier;
    private readonly IDbConnectionFactory _connections;

    public JobRefresher(IEnumerable<IAtsJobSource> sources, AiExtractedJobSource aiSource,
                         ICompanyRepository companies, ICompanyUrlsRepository urls,
                         IJobRepository jobs, IAreaRepository areas,
                         IJobClassifier classifier, IDbConnectionFactory connections)
    {
        _sources = sources.ToDictionary(s => s.AtsType, StringComparer.OrdinalIgnoreCase);
        _aiSource = aiSource;
        _companies = companies;
        _urls = urls;
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

        // Prune URLs that haven't yielded jobs in 30 days. Keeps the cache clean over time.
        var prunedUrls = _urls.DeleteStale(notYieldedDays: 30);

        var all = _companies.GetAll();
        foreach (var c in all)
        {
            ct.ThrowIfCancellationRequested();
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
        var allRaw = new List<RawJobPosting>();
        string sourceType;

        // ── Decision tree ────────────────────────────────────────────────────
        // 1) Native ATS adapter when we have ats_type + ats_slug
        if (!string.IsNullOrEmpty(company.AtsType) && !string.IsNullOrEmpty(company.AtsSlug)
            && _sources.TryGetValue(company.AtsType, out var native))
        {
            allRaw.AddRange(await native.FetchAsync(company.AtsSlug, ct));
            sourceType = native.AtsType;
        }
        else
        {
            sourceType = _aiSource.AtsType;
            var cachedUrls = _urls.GetByCompany(company.Id);

            // 2) Cached job_list URLs — usually the actual jobs page
            var jobListUrls = cachedUrls.Where(u => u.Kind == UrlKind.JobList).Take(3).ToList();
            // 3) Cached department URLs — one-level-deep recursive crawl
            var departmentUrls = cachedUrls.Where(u => u.Kind == UrlKind.Department).Take(10).ToList();
            // 4) Cached careers_root URLs (fallback within cache)
            var rootUrls = cachedUrls.Where(u => u.Kind == UrlKind.CareersRoot).Take(2).ToList();

            var urlsToTry = jobListUrls.Concat(departmentUrls).Concat(rootUrls).ToList();
            if (urlsToTry.Count == 0)
            {
                // 5) Full rediscovery — no cache yet, hit the default careers URL
                var startUrl = company.CareersUrl ?? company.WebsiteUrl ?? $"https://{company.Domain}/careers";
                try
                {
                    var jobs = await _aiSource.FetchForCompanyAsync(company.Id, startUrl, ct);
                    allRaw.AddRange(jobs);
                }
                catch (Exception ex)
                {
                    errors.Add($"[{company.Domain}] {ex.Message}");
                }
            }
            else
            {
                foreach (var u in urlsToTry)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var jobs = await _aiSource.FetchForCompanyAsync(company.Id, u.Url, ct);
                        if (jobs.Count > 0)
                        {
                            allRaw.AddRange(jobs);
                            // marked yielded inside FetchForCompanyAsync
                        }
                        else
                        {
                            _urls.RecordFailure(company.Id, u.Url);
                        }
                    }
                    catch (Exception ex)
                    {
                        _urls.RecordFailure(company.Id, u.Url);
                        errors.Add($"[{company.Domain} via cached {u.Kind}] {ex.Message}");
                    }
                }
            }

            // Free upgrade: if the network listener saw an ATS API call we recognize, persist it on
            // the company so subsequent refreshes use the native adapter (faster, no rate limit).
            UpgradeCompanyAtsIfDetected(company);
        }

        // Dedupe by native ID across runs (a department crawl can hit the same job from multiple URLs).
        var deduped = allRaw
            .GroupBy(j => j.NativeId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var added = 0; var updated = 0;
        var seenJobIds = new HashSet<int>();

        foreach (var r in deduped)
        {
            ct.ThrowIfCancellationRequested();
            var hashKey = $"{sourceType}:{company.Id}:{r.NativeId}";
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

    private static readonly System.Text.RegularExpressions.Regex AtsApiUrlRe = new(
        @"^https?://(?:boards-api\.greenhouse\.io/v1/boards|api\.lever\.co/v0/postings|api\.ashbyhq\.com/posting-api/job-board)/(?<slug>[a-zA-Z0-9-]+)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>If the company has cached ats_api URLs from a previous network-listener probe but no
    /// ats_type/ats_slug set, infer them and update the company. Future refreshes skip Playwright.</summary>
    private void UpgradeCompanyAtsIfDetected(Company company)
    {
        if (!string.IsNullOrEmpty(company.AtsType) && !string.IsNullOrEmpty(company.AtsSlug)) return;

        var atsApiUrls = _urls.GetByCompanyAndKind(company.Id, UrlKind.AtsApi);
        foreach (var u in atsApiUrls)
        {
            var m = AtsApiUrlRe.Match(u.Url);
            if (!m.Success) continue;
            var atsType = u.Url.Contains("greenhouse.io") ? "greenhouse"
                        : u.Url.Contains("lever.co")       ? "lever"
                        : u.Url.Contains("ashbyhq.com")    ? "ashby"
                        : null;
            if (atsType is null) continue;
            var slug = m.Groups["slug"].Value.ToLowerInvariant();
            _companies.SetAtsInfo(company.Id, atsType, slug, u.Url);
            return;
        }
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
