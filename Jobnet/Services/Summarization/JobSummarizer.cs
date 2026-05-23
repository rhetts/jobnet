using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Jobnet.Services.Ai;
using Jobnet.Services.ApiUsage;
using Jobnet.Services.Profiling;
using Jobnet.Services.RateLimit;

namespace Jobnet.Services.Summarization;

public interface IJobSummarizer
{
    /// <summary>Generate and persist a paragraph summary for one job.</summary>
    Task<JobSummaryResult> SummarizeAsync(Job job, CancellationToken ct = default);

    /// <summary>Batch backfill summaries for active jobs with no summary yet. <paramref name="progress"/>,
    /// if supplied, gets a "starting"/"done" pair per job — VMs convert these to run_step_log rows.</summary>
    Task<JobSummaryBatchReport> BackfillAsync(int max = 50, IProgress<Jobnet.Services.Logging.BatchStepProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Regenerate summaries for ALL active jobs, overwriting existing ones.
    /// Use after prompt changes when current summaries are stale.</summary>
    Task<JobSummaryBatchReport> RegenerateAllAsync(int max = 500, IProgress<Jobnet.Services.Logging.BatchStepProgress>? progress = null, CancellationToken ct = default);
}

public sealed class JobSummaryResult
{
    public bool Success { get; init; }
    public string? Summary { get; init; }
    public string? Model { get; init; }
    public string? Error { get; init; }
    public bool Skipped { get; init; }
    public string? SkipReason { get; init; }
}

public sealed class JobSummaryBatchReport
{
    public int Examined { get; set; }
    public int Generated { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public System.Collections.Generic.List<string> Errors { get; } = new();
}

public sealed class JobSummarizer : IJobSummarizer
{
    public const string HttpProvider = "http_fetch";

    private readonly HttpClient _http;
    private readonly IAiClient _ai;
    private readonly IApiUsageTracker _usage;
    private readonly IRateLimiter _rateLimiter;
    private readonly IJobRepository _jobs;

    public JobSummarizer(HttpClient http, IAiClient ai, IApiUsageTracker usage,
                          IRateLimiter rateLimiter, IJobRepository jobs)
    {
        _http = http;
        _ai = ai;
        _usage = usage;
        _rateLimiter = rateLimiter;
        _jobs = jobs;
    }

    public async Task<JobSummaryResult> SummarizeAsync(Job job, CancellationToken ct = default)
    {
        if (!_ai.IsConfigured)
            return new JobSummaryResult { Success = false, Skipped = true, SkipReason = "AI not configured" };

        // Source text: prefer fetching the job detail page if we have a URL, fall back to existing description snippet.
        string sourceText = job.DescriptionSnippet ?? "";
        if (!string.IsNullOrWhiteSpace(job.Url))
        {
            var pageText = await FetchTextAsync(job.Url!, ct);
            if (pageText.Length > sourceText.Length) sourceText = pageText;
        }

        if (sourceText.Length < 60)
            return new JobSummaryResult { Skipped = true, SkipReason = "Not enough source text" };

        if (sourceText.Length > 6000) sourceText = sourceText.Substring(0, 6000);

        var system =
            "You write concise job summaries that describe the actual work, not the company. " +
            "Plain prose only — no fluff, no lists, no markdown, no quotes, no headings. " +
            "Never mention the company name, mission, culture, benefits, perks, DEI statements, " +
            "compensation philosophy, or 'why us'. " +
            "If the source page is mostly company pitch with little actual job info, infer the " +
            "day-to-day work from the title and write a tight summary based on what such a role typically does.";
        var user =
            $"Job title: {job.Title}\n\n" +
            $"Source text:\n{sourceText}\n\n" +
            "Write 2-3 sentences (60-90 words) describing what this person actually does each week. " +
            "Concrete responsibilities, technologies, the kinds of problems they solve, and who they collaborate with. " +
            "Do NOT describe the company. Do NOT list 'qualifications' or 'requirements'. " +
            "Do NOT start with 'This role is...' or 'The successful candidate...' — start with a verb or the responsibility itself. " +
            "If the source is mostly company boilerplate, write a generic-but-specific summary based on the title alone.";

        AiResponse response;
        try
        {
            response = await _ai.CompleteAsync(user, system, maxTokens: 250, ct, task: "summary");
        }
        catch (Exception ex)
        {
            return new JobSummaryResult { Success = false, Error = $"AI call failed: {ex.Message}" };
        }

        var text = (response.Text ?? "").Trim();
        if (text.Length == 0)
            return new JobSummaryResult { Success = false, Error = "Empty AI response" };

        _jobs.SetSummary(job.Id, text, response.Model, DateTime.UtcNow);
        return new JobSummaryResult { Success = true, Summary = text, Model = response.Model };
    }

    public Task<JobSummaryBatchReport> BackfillAsync(int max = 50, IProgress<Jobnet.Services.Logging.BatchStepProgress>? progress = null, CancellationToken ct = default) =>
        RunBatchAsync(_jobs.GetJobsNeedingSummary(max), progress, ct);

    public Task<JobSummaryBatchReport> RegenerateAllAsync(int max = 500, IProgress<Jobnet.Services.Logging.BatchStepProgress>? progress = null, CancellationToken ct = default) =>
        RunBatchAsync(_jobs.GetAll(includeRemoved: false).Take(max).ToList(), progress, ct);

    private async Task<JobSummaryBatchReport> RunBatchAsync(IReadOnlyList<Job> pending,
                                                             IProgress<Jobnet.Services.Logging.BatchStepProgress>? progress,
                                                             CancellationToken ct)
    {
        var report = new JobSummaryBatchReport();
        if (!_ai.IsConfigured)
        {
            report.Errors.Add("AI provider not configured.");
            return report;
        }

        foreach (var job in pending)
        {
            ct.ThrowIfCancellationRequested();
            report.Examined++;
            var name = $"#{job.Id} {Trunc(job.Title, 80)}";
            progress?.Report(new Jobnet.Services.Logging.BatchStepProgress { Name = name, Stage = "starting" });
            try
            {
                var r = await SummarizeAsync(job, ct);
                if (r.Success)
                {
                    report.Generated++;
                    progress?.Report(new Jobnet.Services.Logging.BatchStepProgress { Name = name, Stage = "done", Status = "completed", Added = 1 });
                }
                else if (r.Skipped)
                {
                    report.Skipped++;
                    progress?.Report(new Jobnet.Services.Logging.BatchStepProgress { Name = name, Stage = "done", Status = "skipped", Skipped = 1, ErrorMessage = r.SkipReason });
                }
                else
                {
                    report.Failed++;
                    if (r.Error is not null) report.Errors.Add($"[job {job.Id}] {r.Error}");
                    progress?.Report(new Jobnet.Services.Logging.BatchStepProgress { Name = name, Stage = "done", Status = "failed", Failed = 1, ErrorMessage = r.Error });
                }
            }
            catch (Exception ex)
            {
                report.Failed++;
                report.Errors.Add($"[job {job.Id}] {ex.GetType().Name}: {ex.Message}");
                progress?.Report(new Jobnet.Services.Logging.BatchStepProgress { Name = name, Stage = "done", Status = "failed", Failed = 1, ErrorMessage = $"{ex.GetType().Name}: {ex.Message}" });
            }
        }
        return report;
    }

    private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n - 1) + "…");

    private async Task<string> FetchTextAsync(string url, CancellationToken ct)
    {
        try
        {
            await _rateLimiter.WaitAsync(HttpProvider, ct);
            _usage.RecordCall(HttpProvider);
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return "";
            var html = await resp.Content.ReadAsStringAsync(ct);
            return HtmlTextExtractor.Extract(html, maxChars: 6000);
        }
        catch
        {
            return "";
        }
    }
}
