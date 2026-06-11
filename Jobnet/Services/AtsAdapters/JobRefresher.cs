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
using Jobnet.Services.Location;
using Jobnet.Services.Profile;
using Jobnet.Services.Technology;

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
    private readonly Jobnet.Services.AtsDetection.IAtsDetector _atsDetector;
    private readonly IConfigRepository _config;
    private readonly ITechnologyMatcher _techMatcher;
    private readonly ITechnologyRepository _techs;

    public JobRefresher(IEnumerable<IAtsJobSource> sources, AiExtractedJobSource aiSource,
                         ICompanyRepository companies, ICompanyUrlsRepository urls,
                         IJobRepository jobs, IAreaRepository areas,
                         IJobClassifier classifier, IDbConnectionFactory connections,
                         Jobnet.Services.AtsDetection.IAtsDetector atsDetector,
                         IConfigRepository config,
                         ITechnologyMatcher techMatcher,
                         ITechnologyRepository techs)
    {
        _sources = sources.ToDictionary(s => s.AtsType, StringComparer.OrdinalIgnoreCase);
        _aiSource = aiSource;
        _companies = companies;
        _urls = urls;
        _jobs = jobs;
        _areas = areas;
        _classifier = classifier;
        _connections = connections;
        _atsDetector = atsDetector;
        _config = config;
        _techMatcher = techMatcher;
        _techs = techs;
    }

    /// <summary>Greylist tokens compiled once at the start of a batch run and cached across all
    /// companies / jobs in that pass. Stored per-instance because JobRefresher is a singleton.</summary>
    private System.Collections.Generic.IReadOnlyList<System.Text.RegularExpressions.Regex> _greylistTokens
        = System.Array.Empty<System.Text.RegularExpressions.Regex>();

    private void RefreshGreylistTokens()
    {
        _greylistTokens = GreylistMatcher.Parse(_config.GetOrDefault("profile_greylist_keywords", ""));
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

    public async Task<JobRefreshReport> RefreshAllAsync(int minDaysSinceLastScan = 0, IProgress<JobRefreshProgress>? progress = null, CancellationToken ct = default)
    {
        var scanId = StartScanLog("global");
        var errors = new List<string>();
        var added = 0; var updated = 0; var removed = 0;
        var skipped = 0; var skippedRecent = 0; var failed = 0; var processed = 0;

        // Prune URLs that haven't yielded jobs in 30 days. Keeps the cache clean over time.
        var prunedUrls = _urls.DeleteStale(notYieldedDays: 30);

        // Refresh the greylist regex set once per batch — applied per-new-job below.
        RefreshGreylistTokens();

        DateTime? cutoff = minDaysSinceLastScan > 0
            ? DateTime.UtcNow.AddDays(-minDaysSinceLastScan)
            : null;

        var all = _companies.GetAll();
        // Pre-filter to the eligible list so the progress Total reflects what we'll actually visit.
        var eligible = cutoff.HasValue
            ? all.Where(c => !c.DateLastScan.HasValue || c.DateLastScan.Value <= cutoff.Value).ToList()
            : all.ToList();
        skippedRecent = all.Count - eligible.Count;

        // Order productive companies first. A long refresh may be cancelled before completing —
        // the user is better served by hitting companies that historically have many active jobs
        // (so the refresh re-confirms / surfaces additions there) before grinding through the
        // tail of marketing pages and news sites that yield nothing. Companies with no active
        // jobs yet still get visited last; alphabetical falls out of the StableSort.
        var activeCounts = _jobs.GetActiveCountsByCompany();
        eligible = eligible
            .OrderByDescending(c => activeCounts.TryGetValue(c.Id, out var n) ? n : 0)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var total = eligible.Count;
        var idx = 0;
        foreach (var c in eligible)
        {
            ct.ThrowIfCancellationRequested();
            idx++;

            // Emit "starting" BEFORE the work — critical for the local-llama path where a single
            // company can take 5-15 minutes. Otherwise the UI sits on a stale "done" message for
            // the prior company until this one finishes.
            progress?.Report(new JobRefreshProgress
            {
                Current = idx, Total = total,
                CompanyName = c.Name, CompanyDomain = c.Domain,
                Stage = "starting",
                JobsAddedSoFar = added, JobsUpdatedSoFar = updated, ErrorsSoFar = errors.Count,
            });

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

            progress?.Report(new JobRefreshProgress
            {
                Current = idx, Total = total,
                CompanyName = c.Name, CompanyDomain = c.Domain,
                Stage = "done",
                JobsAddedSoFar = added, JobsUpdatedSoFar = updated, ErrorsSoFar = errors.Count,
            });
        }

        FinishScanLog(scanId, processed, added, removed, errors);
        return new JobRefreshReport
        {
            CompaniesProcessed = processed,
            CompaniesSkippedNoAts = skipped,
            CompaniesSkippedRecent = skippedRecent,
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
            // 1b) Cheap HTTP-only ATS probe before falling back to Playwright + AI extraction.
            // Catches static iframe embeds and redirect-based ATS hosting (most Greenhouse / Lever /
            // Ashby customers). On a hit we persist the result and use the native adapter, saving
            // ~3K AI tokens per company. On a miss we proceed to AI as today.
            IAtsJobSource? detectedNative = null;
            string? detectedSlug = null;
            try
            {
                var det = await _atsDetector.DetectViaHttpAsync(company, ct);
                if (det.AtsType is not null && det.AtsSlug is not null
                    && _sources.TryGetValue(det.AtsType, out var maybeNative))
                {
                    _companies.SetAtsInfo(company.Id, det.AtsType, det.AtsSlug, det.ResolvedCareersUrl);
                    detectedNative = maybeNative;
                    detectedSlug = det.AtsSlug;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Detection failures are non-fatal — fall through to AI extraction.
                errors.Add($"[{company.Domain}] ats-detect: {ex.GetType().Name}: {ex.Message}");
            }

            if (detectedNative is not null && detectedSlug is not null)
            {
                allRaw.AddRange(await detectedNative.FetchAsync(detectedSlug, ct));
                sourceType = detectedNative.AtsType;
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
                    var jobs = await _aiSource.FetchForCompanyAsync(company, startUrl, ct);
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
                        var jobs = await _aiSource.FetchForCompanyAsync(company, u.Url, ct);
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
        }

        // Some ATS boards are shared across brands (e.g. Match Group's Lever slug lists
        // Hinge, Tinder, Plenty of Fish, etc.). If the company has a department filter set,
        // keep only postings whose ATS-reported department matches.
        if (!string.IsNullOrWhiteSpace(company.AtsDepartmentFilter))
        {
            var brand = company.AtsDepartmentFilter!;
            allRaw = allRaw
                .Where(r => string.Equals(r.Department, brand, StringComparison.OrdinalIgnoreCase))
                .ToList();
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

            // Vancouver-area gate: skip jobs whose location is clearly elsewhere.
            // Blank/unknown locations pass through (handled inside LocationMatcher).
            if (!LocationMatcher.IsVancouverArea(r.Location)) continue;

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
                SalaryMin = r.SalaryMin,
                SalaryMax = r.SalaryMax,
                SalaryCurrency = r.SalaryCurrency,
                SalaryPeriod = r.SalaryPeriod,
                InterestLevel = InterestLevel.Neutral,
                DateFirstSeen = DateTime.UtcNow,
                DateLastSeen = DateTime.UtcNow,
                IsActive = true,
            };

            var (id, wasNew) = _jobs.Upsert(job, hashKey, hashTier: 1);
            seenJobIds.Add(id);

            // Tag with detected technologies from title + description snippet. We re-run on every
            // upsert (not just new rows) so updated descriptions pick up newly-added vocabulary.
            // Job summary isn't included here because it's generated asynchronously by a separate
            // step — the technology backfill CLI re-runs once summaries exist.
            var techText = (r.Title ?? "") + "\n" + (r.DescriptionSnippet ?? "");
            var techIds = _techMatcher.Match(techText);
            _techs.SetForJob(id, techIds);

            if (wasNew)
            {
                added++;
                // Greylist applies only to brand-new rows — existing rows preserve whatever
                // interest the user has set. company.Name comes from the Company we already loaded.
                if (_greylistTokens.Count > 0 &&
                    GreylistMatcher.MatchesAny(_greylistTokens, job.Title, job.DescriptionSnippet, company.Name))
                {
                    _jobs.SetInterestLevel(id, InterestLevel.NotInteresting);
                }
            }
            else updated++;
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
