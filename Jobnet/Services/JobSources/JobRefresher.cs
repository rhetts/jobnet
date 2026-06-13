using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
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

namespace Jobnet.Services.JobSources;

public sealed class JobRefresher : IJobRefresher
{
    private readonly Dictionary<string, IJobSource> _sources;
    private readonly AiFallbackJobSource _aiSource;
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
    private readonly Jobnet.Services.Logging.IRunLogger _runs;

    public JobRefresher(IEnumerable<IJobSource> sources, AiFallbackJobSource aiSource,
                         ICompanyRepository companies, ICompanyUrlsRepository urls,
                         IJobRepository jobs, IAreaRepository areas,
                         IJobClassifier classifier, IDbConnectionFactory connections,
                         Jobnet.Services.AtsDetection.IAtsDetector atsDetector,
                         IConfigRepository config,
                         ITechnologyMatcher techMatcher,
                         ITechnologyRepository techs,
                         Jobnet.Services.Logging.IRunLogger runs)
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
        _runs = runs;
    }

    /// <summary>Threshold of consecutive empty/failed refreshes before we auto-clear a company's
    /// ats_slug. Two is enough to recover from a stale slug without thrashing — a single empty
    /// day is normal, two in a row almost always means the slug is wrong (Alida moved off Lever,
    /// 9 Mothers was a bogus detection).</summary>
    private const int StaleSlugThreshold = 2;

    /// <summary>Greylist tokens compiled once at the start of a batch run and cached across all
    /// companies / jobs in that pass. Stored per-instance because JobRefresher is a singleton.</summary>
    private System.Collections.Generic.IReadOnlyList<System.Text.RegularExpressions.Regex> _greylistTokens
        = System.Array.Empty<System.Text.RegularExpressions.Regex>();

    private void RefreshGreylistTokens()
    {
        _greylistTokens = GreylistMatcher.Parse(_config.GetOrDefault("profile_greylist_keywords", ""));
    }

    public async Task<JobRefreshReport> RefreshAsync(Company company, CancellationToken ct = default, long? runId = null)
    {
        var scanId = StartScanLog(company.Domain);
        var errors = new List<string>();
        var added = 0; var updated = 0; var removed = 0;
        var skipped = 0; var failed = 0;
        var processed = 0;

        try
        {
            (added, updated, removed, _) = await RefreshOneAsync(company, errors, ct, runId);
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

    public async Task<JobRefreshReport> RefreshAllAsync(int minDaysSinceLastScan = 0, IProgress<JobRefreshProgress>? progress = null, CancellationToken ct = default, long? runId = null)
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
        // Inactive companies are dropped entirely — they're acquired / defunct / wrong-domain entries
        // we keep around only so their historical jobs survive. They never count against the
        // skipped-recent tally because the user has explicitly retired them.
        var active = all.Where(c => c.IsActive).ToList();
        var eligible = cutoff.HasValue
            ? active.Where(c => !c.DateLastScan.HasValue || c.DateLastScan.Value <= cutoff.Value).ToList()
            : active.ToList();
        skippedRecent = active.Count - eligible.Count;

        // Order: native-ATS companies first, then by active-job count, then alphabetically.
        //
        // A long refresh can be cancelled before completing — and native adapters are 10–100x
        // cheaper than the Playwright + AI fallback (no JS render, no AI tokens, one HTTP call).
        // Front-loading them means: (a) the highest-confidence sources are covered first; (b) a
        // cancelled run still produces a useful job list; (c) AI quota burns get pushed to the
        // tail, where they're easier to defer or split across runs.
        //
        // Within each group, productive companies still come first so historical contributors
        // re-confirm before low-yield marketing pages.
        var activeCounts = _jobs.GetActiveCountsByCompany();
        eligible = eligible
            .OrderByDescending(c => !string.IsNullOrEmpty(c.AtsType) && !string.IsNullOrEmpty(c.AtsSlug))
            .ThenByDescending(c => activeCounts.TryGetValue(c.Id, out var n) ? n : 0)
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

            string? outcomeKind = null;
            string? errorMessage = null;
            try
            {
                var (a, u, r, ok) = await RefreshOneAsync(c, errors, ct, runId);
                added += a; updated += u; removed += r;
                outcomeKind = ok;
                processed++;
            }
            catch (NoAdapterException)
            {
                skipped++;
                outcomeKind = Logging.OutcomeKind.NoAdapter;
            }
            catch (OperationCanceledException)
            {
                outcomeKind = Logging.OutcomeKind.CancelledUser;
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"[{c.Domain}] {ex.Message}");
                errorMessage = $"{ex.GetType().Name}: {ex.Message}";
                // Classify the exception so the step row gets a useful outcome_kind even though
                // the refresher itself didn't reach the per-stage classification path.
                outcomeKind = ClassifyException(ex);
                failed++;
            }

            progress?.Report(new JobRefreshProgress
            {
                Current = idx, Total = total,
                CompanyName = c.Name, CompanyDomain = c.Domain,
                Stage = "done",
                JobsAddedSoFar = added, JobsUpdatedSoFar = updated, ErrorsSoFar = errors.Count,
                OutcomeKind = outcomeKind,
                ErrorMessage = errorMessage,
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

    private async Task<(int Added, int Updated, int Removed, string? OutcomeKind)> RefreshOneAsync(Company company, List<string> errors, CancellationToken ct, long? runId)
    {
        // Stamp the AsyncLocal scope so any api_call_log / refresh_attempt rows written deeper
        // in the pipeline automatically pick up the current run + company.
        using var _scope = Logging.RefreshContext.BeginScope(runId, company.Id);

        var allRaw = new List<RawJobPosting>();
        string sourceType;      // ats_type-style key used in the job hash
        string sourceStage;     // refresh_attempt.stage value — finer-grained for telemetry
        var stageHadFailure = false;
        var lastStageResult = Logging.AttemptResult.Success;
        var lastStageHttp = (int?)null;

        // ── Decision tree ────────────────────────────────────────────────────
        // 1) Native ATS adapter when we have ats_type + ats_slug
        if (!string.IsNullOrEmpty(company.AtsType) && !string.IsNullOrEmpty(company.AtsSlug)
            && _sources.TryGetValue(company.AtsType, out var native))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            sourceStage = $"ats_{native.AtsType}";
            try
            {
                var jobs = await native.FetchAsync(company.AtsSlug, ct);
                sw.Stop();
                allRaw.AddRange(jobs);
                lastStageResult = jobs.Count == 0 ? Logging.AttemptResult.Empty : Logging.AttemptResult.Success;
                _runs.LogAttempt(runId, company.Id, Logging.AttemptStage.AtsApi, native.AtsType,
                                  lastStageResult, null, jobs.Count, sw.ElapsedMilliseconds, null);
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException hex)
            {
                sw.Stop();
                stageHadFailure = true;
                var code = (int?)hex.StatusCode;
                lastStageHttp = code;
                lastStageResult = (code is >= 400 and < 500) ? Logging.AttemptResult.Http4xx
                                : (code is >= 500 and < 600) ? Logging.AttemptResult.Http5xx
                                : Logging.AttemptResult.ParseException;
                _runs.LogAttempt(runId, company.Id, Logging.AttemptStage.AtsApi, native.AtsType,
                                  lastStageResult, code, 0, sw.ElapsedMilliseconds, hex.Message);
                errors.Add($"[{company.Domain}] {hex.Message}");
            }
            catch (Exception ex)
            {
                sw.Stop();
                stageHadFailure = true;
                lastStageResult = Logging.AttemptResult.ParseException;
                _runs.LogAttempt(runId, company.Id, Logging.AttemptStage.AtsApi, native.AtsType,
                                  lastStageResult, null, 0, sw.ElapsedMilliseconds,
                                  $"{ex.GetType().Name}: {ex.Message}");
                errors.Add($"[{company.Domain}] {ex.Message}");
            }
            sourceType = native.AtsType;
        }
        else
        {
            // 1b) Cheap HTTP-only ATS probe before falling back to Playwright + AI extraction.
            IJobSource? detectedNative = null;
            string? detectedSlug = null;
            var detectSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var det = await _atsDetector.DetectViaHttpAsync(company, ct);
                detectSw.Stop();
                if (det.AtsType is not null && det.AtsSlug is not null
                    && _sources.TryGetValue(det.AtsType, out var maybeNative))
                {
                    _companies.SetAtsInfo(company.Id, det.AtsType, det.AtsSlug, det.ResolvedCareersUrl);
                    detectedNative = maybeNative;
                    detectedSlug = det.AtsSlug;
                    _runs.LogAttempt(runId, company.Id, Logging.AttemptStage.DetectAts, det.Source,
                                      Logging.AttemptResult.Success, null, 0, detectSw.ElapsedMilliseconds, null);
                }
                else
                {
                    _runs.LogAttempt(runId, company.Id, Logging.AttemptStage.DetectAts, det.Source,
                                      Logging.AttemptResult.Empty, null, 0, detectSw.ElapsedMilliseconds, det.Notes);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                detectSw.Stop();
                _runs.LogAttempt(runId, company.Id, Logging.AttemptStage.DetectAts, null,
                                  Logging.AttemptResult.ParseException, null, 0, detectSw.ElapsedMilliseconds,
                                  $"{ex.GetType().Name}: {ex.Message}");
                errors.Add($"[{company.Domain}] ats-detect: {ex.GetType().Name}: {ex.Message}");
            }

            if (detectedNative is not null && detectedSlug is not null)
            {
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                sourceStage = $"ats_{detectedNative.AtsType}";
                try
                {
                    var jobs = await detectedNative.FetchAsync(detectedSlug, ct);
                    sw2.Stop();
                    allRaw.AddRange(jobs);
                    _runs.LogAttempt(runId, company.Id, Logging.AttemptStage.AtsApi, detectedNative.AtsType,
                                      jobs.Count == 0 ? Logging.AttemptResult.Empty : Logging.AttemptResult.Success,
                                      null, jobs.Count, sw2.ElapsedMilliseconds, null);
                }
                catch (Exception ex)
                {
                    sw2.Stop();
                    stageHadFailure = true;
                    _runs.LogAttempt(runId, company.Id, Logging.AttemptStage.AtsApi, detectedNative.AtsType,
                                      Logging.AttemptResult.ParseException, null, 0, sw2.ElapsedMilliseconds,
                                      $"{ex.GetType().Name}: {ex.Message}");
                    errors.Add($"[{company.Domain}] {ex.Message}");
                }
                sourceType = detectedNative.AtsType;
            }
            else
            {
            sourceType = _aiSource.AtsType;
            sourceStage = Logging.AttemptStage.AiExtract;
            var cachedUrls = _urls.GetByCompany(company.Id);

            var jobListUrls = cachedUrls.Where(u => u.Kind == UrlKind.JobList).Take(3).ToList();
            var departmentUrls = cachedUrls.Where(u => u.Kind == UrlKind.Department).Take(10).ToList();
            var rootUrls = cachedUrls.Where(u => u.Kind == UrlKind.CareersRoot).Take(2).ToList();

            var urlsToTry = jobListUrls.Concat(departmentUrls).Concat(rootUrls).ToList();
            if (urlsToTry.Count == 0)
            {
                var startUrl = company.CareersUrl ?? company.WebsiteUrl ?? $"https://{company.Domain}/careers";
                var sw3 = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var jobs = await _aiSource.FetchForCompanyAsync(company, startUrl, ct);
                    sw3.Stop();
                    allRaw.AddRange(jobs);
                    // AiFallbackJobSource exposes the actual stage that produced the result —
                    // could be hand_written, jsonld, selectors, or ai_extract. Use it for
                    // source_stage so parser-stats can distinguish.
                    sourceStage = !string.IsNullOrEmpty(_aiSource.LastParserUsed)
                        ? $"hand_written:{_aiSource.LastParserUsed}"
                        : Logging.AttemptStage.AiExtract;
                    _runs.LogAttempt(runId, company.Id, sourceStage, startUrl,
                                      jobs.Count == 0 ? Logging.AttemptResult.Empty : Logging.AttemptResult.Success,
                                      null, jobs.Count, sw3.ElapsedMilliseconds, null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    sw3.Stop();
                    stageHadFailure = true;
                    _runs.LogAttempt(runId, company.Id, Logging.AttemptStage.AiExtract, startUrl,
                                      Logging.AttemptResult.ParseException, null, 0, sw3.ElapsedMilliseconds,
                                      $"{ex.GetType().Name}: {ex.Message}");
                    errors.Add($"[{company.Domain}] {ex.Message}");
                }
            }
            else
            {
                foreach (var u in urlsToTry)
                {
                    ct.ThrowIfCancellationRequested();
                    var sw4 = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var jobs = await _aiSource.FetchForCompanyAsync(company, u.Url, ct);
                        sw4.Stop();
                        if (jobs.Count > 0)
                        {
                            allRaw.AddRange(jobs);
                            sourceStage = !string.IsNullOrEmpty(_aiSource.LastParserUsed)
                                ? $"hand_written:{_aiSource.LastParserUsed}"
                                : Logging.AttemptStage.AiExtract;
                            _runs.LogAttempt(runId, company.Id, Logging.AttemptStage.CachedUrl, u.Kind.ToString(),
                                              Logging.AttemptResult.Success, null, jobs.Count, sw4.ElapsedMilliseconds, null);
                        }
                        else
                        {
                            _urls.RecordFailure(company.Id, u.Url);
                            _runs.LogAttempt(runId, company.Id, Logging.AttemptStage.CachedUrl, u.Kind.ToString(),
                                              Logging.AttemptResult.Empty, null, 0, sw4.ElapsedMilliseconds, null);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        sw4.Stop();
                        _urls.RecordFailure(company.Id, u.Url);
                        _runs.LogAttempt(runId, company.Id, Logging.AttemptStage.CachedUrl, u.Kind.ToString(),
                                          Logging.AttemptResult.ParseException, null, 0, sw4.ElapsedMilliseconds,
                                          $"{ex.GetType().Name}: {ex.Message}");
                        errors.Add($"[{company.Domain} via cached {u.Kind}] {ex.Message}");
                    }
                }
            }

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
                SourceStage = sourceStage,
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

        // Persist refresh health rollup: jobs found, success/fail streak. Drives auto-clear of
        // stale slugs and 0-yield drift detection in the next refresh.
        var totalSeen = added + updated;
        _companies.RecordRefreshResult(company.Id, totalSeen, stageHadFailure);

        // Auto-clear stale slug at threshold. ConsecutiveFailures was just incremented if this
        // refresh was empty/failed, so the threshold check uses the *new* expected value (+1).
        var projectedFailures = (totalSeen == 0 || stageHadFailure)
            ? company.ConsecutiveFailures + 1 : 0;
        if (projectedFailures >= StaleSlugThreshold
            && !string.IsNullOrEmpty(company.AtsSlug)
            && lastStageResult == Logging.AttemptResult.Http4xx)
        {
            _companies.ClearAtsSlug(company.Id,
                $"{projectedFailures} consecutive 4xx (last HTTP {lastStageHttp})");
            errors.Add($"[{company.Domain}] cleared stale {company.AtsType} slug '{company.AtsSlug}' after {projectedFailures} 4xx failures");
        }

        // 0-yield drift: previously productive, now empty (and not a network failure). Likely the
        // company moved to a different page or ATS shape. Clear the slug so detect-ats re-runs.
        if (totalSeen == 0
            && company.LastRefreshJobsCount is > 0
            && !stageHadFailure
            && !string.IsNullOrEmpty(company.AtsSlug))
        {
            _companies.ClearAtsSlug(company.Id,
                $"0-yield drift: was {company.LastRefreshJobsCount}, now 0");
            errors.Add($"[{company.Domain}] 0-yield drift: was producing {company.LastRefreshJobsCount} jobs, now 0 — re-detect queued");
        }

        // Classify the company-level outcome_kind for the run_step_log row.
        var outcome = ClassifyOutcome(totalSeen, allRaw.Count, stageHadFailure,
                                       lastStageResult, lastStageHttp);
        return (added, updated, removedCount, outcome);
    }

    /// <summary>Map the per-company refresh result into a step-level outcome_kind. Distinguishes
    /// "API returned jobs but all got filtered" from "API returned nothing" from "fetch failed",
    /// since those need different remediation.</summary>
    private static string ClassifyOutcome(int seen, int rawCount, bool hadFailure,
                                           string lastStageResult, int? lastHttp)
    {
        if (seen > 0) return Logging.OutcomeKind.Success;
        if (hadFailure)
        {
            return lastStageResult switch
            {
                Logging.AttemptResult.Http4xx     => Logging.OutcomeKind.Fetch4xx,
                Logging.AttemptResult.Http5xx     => Logging.OutcomeKind.Fetch5xx,
                Logging.AttemptResult.Timeout     => Logging.OutcomeKind.FetchTimeout,
                _                                 => Logging.OutcomeKind.ParseException,
            };
        }
        // No failure, no jobs persisted. Either the API returned nothing or location/dept filters
        // dropped everything. The raw count before filtering tells us which.
        if (rawCount > 0) return Logging.OutcomeKind.AllJobsLocationFiltered;
        return Logging.OutcomeKind.ApiReturnedEmpty;
    }

    /// <summary>Fallback classifier for exceptions that escape RefreshOneAsync entirely. Coarser
    /// than the per-stage classifier because we don't have the structured stage state at this
    /// point — just the exception type/message.</summary>
    private static string ClassifyException(Exception ex)
    {
        var msg = (ex.Message ?? "").ToLowerInvariant();
        if (ex is HttpRequestException hre)
        {
            var c = (int?)hre.StatusCode;
            if (c is >= 400 and < 500) return Logging.OutcomeKind.Fetch4xx;
            if (c is >= 500 and < 600) return Logging.OutcomeKind.Fetch5xx;
        }
        if (ex is TaskCanceledException || msg.Contains("timeout")) return Logging.OutcomeKind.FetchTimeout;
        if (msg.Contains("403") || msg.Contains("forbidden")) return Logging.OutcomeKind.FetchBlocked;
        return Logging.OutcomeKind.ParseException;
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
