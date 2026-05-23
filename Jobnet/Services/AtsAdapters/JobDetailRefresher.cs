using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Jobnet.Data;
using Jobnet.Data.Repositories;
using Jobnet.Services.ApiUsage;
using Jobnet.Services.RateLimit;

namespace Jobnet.Services.AtsAdapters;

public interface IJobDetailRefresher
{
    Task<JobDetailRefreshReport> RefreshExistingAsync(int max = 200,
        IProgress<Jobnet.Services.Logging.BatchStepProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed class JobDetailRefreshReport
{
    public int Examined { get; set; }
    public int Updated  { get; set; }
    public int NoChange { get; set; }
    public int Failed   { get; set; }
    public List<string> Errors { get; } = new();
}

/// <summary>
/// For each active job with a URL, re-fetch the detail page and update salary/employment-type
/// fields from any JSON-LD JobPosting embedded on the page. Does NOT discover new jobs.
/// </summary>
public sealed class JobDetailRefresher : IJobDetailRefresher
{
    public const string HttpProvider = "http_fetch";

    private readonly HttpClient _http;
    private readonly IRateLimiter _rateLimiter;
    private readonly IApiUsageTracker _usage;
    private readonly IDbConnectionFactory _connections;

    public JobDetailRefresher(HttpClient http, IRateLimiter rateLimiter,
                                IApiUsageTracker usage, IDbConnectionFactory connections)
    {
        _http = http;
        _rateLimiter = rateLimiter;
        _usage = usage;
        _connections = connections;
    }

    public async Task<JobDetailRefreshReport> RefreshExistingAsync(int max = 200,
        IProgress<Jobnet.Services.Logging.BatchStepProgress>? progress = null,
        CancellationToken ct = default)
    {
        var report = new JobDetailRefreshReport();

        // Pick the jobs that most stand to gain from an update: missing salary or description,
        // active, has a URL. Newest first.
        using var conn = _connections.Open();
        var rows = conn.Query<(int Id, string Url, int? SalaryMin, int? SalaryMax,
                               string? Currency, string? Period, string? Description,
                               string? EmploymentType)>(@"
            SELECT id AS Id, url AS Url,
                   salary_min AS SalaryMin, salary_max AS SalaryMax,
                   salary_currency AS Currency, salary_period AS Period,
                   description_snippet AS Description,
                   employment_type AS EmploymentType
            FROM jobs
            WHERE is_active = 1 AND url IS NOT NULL AND LENGTH(url) > 0
            ORDER BY date_first_seen DESC
            LIMIT @max", new { max });

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            report.Examined++;
            var stepName = $"#{row.Id} {(row.Url.Length <= 90 ? row.Url : row.Url.Substring(0, 89) + "…")}";
            progress?.Report(new Jobnet.Services.Logging.BatchStepProgress { Name = stepName, Stage = "starting" });
            try
            {
                var html = await FetchHtmlAsync(row.Url, ct);
                if (string.IsNullOrEmpty(html))
                {
                    report.NoChange++;
                    progress?.Report(new Jobnet.Services.Logging.BatchStepProgress { Name = stepName, Stage = "done", Status = "skipped", Skipped = 1, ErrorMessage = "empty fetch" });
                    continue;
                }

                var postings = JsonLdJobExtractor.Extract(html, row.Url);
                if (postings.Count == 0)
                {
                    report.NoChange++;
                    progress?.Report(new Jobnet.Services.Logging.BatchStepProgress { Name = stepName, Stage = "done", Status = "skipped", Skipped = 1, ErrorMessage = "no JSON-LD posting" });
                    continue;
                }
                var p = postings[0]; // detail pages typically have one JobPosting

                // Only fill blanks — never blow away good data we already have.
                var newMin   = row.SalaryMin     ?? p.SalaryMin;
                var newMax   = row.SalaryMax     ?? p.SalaryMax;
                var newCurr  = row.Currency      ?? p.SalaryCurrency;
                var newPer   = row.Period        ?? p.SalaryPeriod;
                var newDesc  = !string.IsNullOrWhiteSpace(row.Description) ? row.Description : p.DescriptionSnippet;
                var newEmp   = row.EmploymentType is null or "unknown" or "" ? (p.EmploymentType ?? row.EmploymentType) : row.EmploymentType;

                var changed = newMin   != row.SalaryMin
                           || newMax   != row.SalaryMax
                           || newCurr  != row.Currency
                           || newPer   != row.Period
                           || newDesc  != row.Description
                           || newEmp   != row.EmploymentType;

                if (!changed)
                {
                    report.NoChange++;
                    progress?.Report(new Jobnet.Services.Logging.BatchStepProgress { Name = stepName, Stage = "done", Status = "skipped", Skipped = 1, ErrorMessage = "no new info" });
                    continue;
                }

                conn.Execute(@"
                    UPDATE jobs SET
                        salary_min      = @newMin,
                        salary_max      = @newMax,
                        salary_currency = @newCurr,
                        salary_period   = @newPer,
                        description_snippet = @newDesc,
                        employment_type = @newEmp
                    WHERE id = @id",
                    new { id = row.Id, newMin, newMax, newCurr, newPer, newDesc, newEmp });
                report.Updated++;
                progress?.Report(new Jobnet.Services.Logging.BatchStepProgress { Name = stepName, Stage = "done", Status = "completed", Updated = 1 });
            }
            catch (Exception ex)
            {
                report.Failed++;
                report.Errors.Add($"[job {row.Id}] {ex.GetType().Name}: {ex.Message}");
                progress?.Report(new Jobnet.Services.Logging.BatchStepProgress { Name = stepName, Stage = "done", Status = "failed", Failed = 1, ErrorMessage = $"{ex.GetType().Name}: {ex.Message}" });
            }
        }
        return report;
    }

    private async Task<string> FetchHtmlAsync(string url, CancellationToken ct)
    {
        try
        {
            await _rateLimiter.WaitAsync(HttpProvider, ct);
            _usage.RecordCall(HttpProvider);
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return "";
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return "";
        }
    }
}
