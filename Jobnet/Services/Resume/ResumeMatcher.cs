using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Jobnet.Services.Ai;
using UglyToad.PdfPig;

namespace Jobnet.Services.Resume;

public interface IResumeMatcher
{
    /// <summary>Load a PDF resume from disk, extract text, and store it.</summary>
    Task<ResumeUploadResult> UploadResumeAsync(string pdfPath, CancellationToken ct = default);

    /// <summary>Score every active job against the stored resume. Updates jobs in place. If
    /// <paramref name="progress"/> is supplied, fires a "starting"/"done" pair per batch — VMs
    /// convert these into run_step_log rows so the Steps pane shows per-batch detail.</summary>
    Task<ResumeMatchReport> MatchAllAsync(int max = 500, int batchSize = 10,
        IProgress<Jobnet.Services.Logging.BatchStepProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>Returns the stored resume text (or null if none uploaded).</summary>
    string? GetStoredResume();
    string? GetStoredResumeSourcePath();
}

public sealed class ResumeUploadResult
{
    public bool Success { get; init; }
    public int Pages { get; init; }
    public int Characters { get; init; }
    public string? Error { get; init; }
}

public sealed class ResumeMatchReport
{
    public int JobsExamined { get; set; }
    public int JobsScored   { get; set; }
    public int JobsFailed   { get; set; }
    public List<string> Errors { get; } = new();
    public string? LastRawResponse { get; set; }
}

public sealed class ResumeMatcher : IResumeMatcher
{
    private readonly IAiClient _ai;
    private readonly IConfigRepository _config;
    private readonly IJobRepository _jobs;
    private readonly ICompanyRepository _companies;
    private readonly ILevelRepository _levels;
    private readonly IAreaRepository _areas;

    public ResumeMatcher(IAiClient ai, IConfigRepository config, IJobRepository jobs,
                         ICompanyRepository companies, ILevelRepository levels, IAreaRepository areas)
    {
        _ai = ai;
        _config = config;
        _jobs = jobs;
        _companies = companies;
        _levels = levels;
        _areas = areas;
    }

    /// <summary>Resolve the candidate's stored preferences into a prompt block. Empty if all
    /// preferences are unset.</summary>
    private string BuildProfileBlock()
    {
        var sb = new StringBuilder();
        try
        {
            var areaIds = ParseIdSet(_config.GetOrDefault("profile_preferred_area_ids", "[]"));
            var levelIds = ParseIdSet(_config.GetOrDefault("profile_preferred_level_ids", "[]"));
            var boost = _config.GetOrDefault("profile_boost_keywords", "").Trim();

            if (areaIds.Count > 0)
            {
                var names = _areas.GetAll().Where(a => areaIds.Contains(a.Id)).Select(a => a.Name).ToList();
                if (names.Count > 0) sb.AppendLine($"Preferred areas: {string.Join(", ", names)}");
            }
            if (levelIds.Count > 0)
            {
                var names = _levels.GetAll().Where(l => levelIds.Contains(l.Id)).Select(l => l.Name).ToList();
                if (names.Count > 0) sb.AppendLine($"Preferred levels: {string.Join(", ", names)}");
            }
            if (!string.IsNullOrEmpty(boost))
            {
                var tokens = boost.Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
                if (tokens.Count > 0) sb.AppendLine($"Boost keywords: {string.Join(", ", tokens)}");
            }
        }
        catch { /* preferences are best-effort */ }
        return sb.ToString();
    }

    private static HashSet<int> ParseIdSet(string json)
    {
        var set = new HashSet<int>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var e in doc.RootElement.EnumerateArray())
                    if (e.TryGetInt32(out var i)) set.Add(i);
        }
        catch { }
        return set;
    }

    public string? GetStoredResume() => _config.Get("resume_text");
    public string? GetStoredResumeSourcePath() => _config.Get("resume_source_path");

    public Task<ResumeUploadResult> UploadResumeAsync(string pdfPath, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(pdfPath))
                return Task.FromResult(new ResumeUploadResult { Success = false, Error = $"File not found: {pdfPath}" });

            int pages = 0;
            var sb = new StringBuilder();
            using (var doc = PdfDocument.Open(pdfPath))
            {
                pages = doc.NumberOfPages;
                foreach (var p in doc.GetPages())
                {
                    sb.AppendLine(p.Text);
                    sb.AppendLine();
                }
            }

            var text = NormalizeWhitespace(sb.ToString());
            if (text.Length < 100)
                return Task.FromResult(new ResumeUploadResult { Success = false, Error = $"Extracted only {text.Length} chars — PDF may be image-based or empty." });

            _config.Set("resume_text", text);
            _config.Set("resume_source_path", pdfPath);
            _config.Set("resume_uploaded_at", DateTime.UtcNow.ToString("o"));
            _jobs.ClearAllResumeMatches();

            return Task.FromResult(new ResumeUploadResult { Success = true, Pages = pages, Characters = text.Length });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ResumeUploadResult { Success = false, Error = $"{ex.GetType().Name}: {ex.Message}" });
        }
    }

    public async Task<ResumeMatchReport> MatchAllAsync(int max = 500, int batchSize = 10,
        IProgress<Jobnet.Services.Logging.BatchStepProgress>? progress = null,
        CancellationToken ct = default)
    {
        var report = new ResumeMatchReport();
        var resume = GetStoredResume();
        if (string.IsNullOrWhiteSpace(resume))
        {
            report.Errors.Add("No resume uploaded.");
            return report;
        }
        if (!_ai.IsConfigured)
        {
            report.Errors.Add("AI provider not configured.");
            return report;
        }

        // Cap resume length sent to the model — most resumes are 2-4KB anyway.
        if (resume!.Length > 6000) resume = resume.Substring(0, 6000);

        var companyNames = _companies.GetAll().ToDictionary(c => c.Id, c => c.Name);
        var jobs = _jobs.GetAll(includeRemoved: false)
            .Where(j => !j.ResumeMatchScore.HasValue)
            .Take(max)
            .ToList();
        report.JobsExamined = jobs.Count;

        foreach (var batch in Chunk(jobs, batchSize))
        {
            ct.ThrowIfCancellationRequested();
            var stepName = $"Batch starting at job #{batch[0].Id} ({batch.Count} jobs)";
            progress?.Report(new Jobnet.Services.Logging.BatchStepProgress { Name = stepName, Stage = "starting" });
            try
            {
                var scored = await ScoreBatchAsync(resume!, batch, companyNames, ct);
                foreach (var s in scored)
                {
                    _jobs.SetResumeMatch(s.JobId, s.Value, s.Reason);
                    report.JobsScored++;
                }
                progress?.Report(new Jobnet.Services.Logging.BatchStepProgress
                {
                    Name = stepName, Stage = "done", Status = "completed",
                    Added = scored.Count, Failed = batch.Count - scored.Count
                });
            }
            catch (Exception ex)
            {
                report.JobsFailed += batch.Count;
                report.Errors.Add($"Batch starting at job {batch[0].Id}: {ex.GetType().Name}: {ex.Message}");
                progress?.Report(new Jobnet.Services.Logging.BatchStepProgress
                {
                    Name = stepName, Stage = "done", Status = "failed",
                    Failed = batch.Count, ErrorMessage = $"{ex.GetType().Name}: {ex.Message}"
                });
            }
        }
        return report;
    }

    /// <summary>Diagnostic — last raw AI response surfaced for debugging when scores come back empty.</summary>
    public string? LastRawResponse { get; private set; }

    private async Task<IReadOnlyList<Score>> ScoreBatchAsync(
        string resume, IReadOnlyList<Job> batch, Dictionary<int, string> companyNames, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var j in batch)
        {
            companyNames.TryGetValue(j.CompanyId, out var cname);
            sb.AppendLine($"--- JOB {j.Id} ---");
            sb.AppendLine($"Title: {j.Title}");
            sb.AppendLine($"Company: {cname}");
            if (!string.IsNullOrWhiteSpace(j.Location)) sb.AppendLine($"Location: {j.Location}");
            if (!string.IsNullOrWhiteSpace(j.Summary)) sb.AppendLine($"Summary: {j.Summary}");
            else if (!string.IsNullOrWhiteSpace(j.DescriptionSnippet))
                sb.AppendLine($"Description: {Trunc(j.DescriptionSnippet!, 500)}");
            sb.AppendLine();
        }

        var system =
            "You score how well a candidate's resume matches a job posting on a 0-100 scale. " +
            "Output STRICT JSON only — no prose, no markdown. Schema:\n" +
            "{ \"scores\": [\n" +
            "  { \"job_id\": 123, \"score\": 78, \"reason\": \"short one-sentence rationale\" }\n" +
            "] }\n" +
            "\n" +
            "Scoring rubric:\n" +
            "- 90-100: ideal — title, seniority, tech stack and domain all match the resume's last role.\n" +
            "- 70-89:  strong fit — most skills overlap; minor stretch in seniority or one area.\n" +
            "- 50-69:  plausible — significant overlap but meaningful gaps (different stack, off by 1-2 levels).\n" +
            "- 30-49:  weak — same broad discipline only; tech/seniority mostly don't match.\n" +
            "- 0-29:   poor — different discipline entirely, or seniority way off.\n" +
            "\n" +
            "WEIGHTING: when CANDIDATE PREFERENCES are provided, prefer jobs whose area / level match them. " +
            "Penalize jobs that violate the preferred-level range. When BOOST KEYWORDS are listed, add " +
            "+5 to +15 to the score for each meaningful keyword that appears in the job. " +
            "Be honest. The user will sort by score, so spread the distribution — do NOT cluster everything in 60-80.\n" +
            "Keep 'reason' under 25 words.";

        var profileBlock = BuildProfileBlock();
        var user =
            "RESUME:\n" + resume + "\n\n" +
            (string.IsNullOrEmpty(profileBlock) ? "" : "CANDIDATE PREFERENCES:\n" + profileBlock + "\n") +
            "JOBS TO SCORE:\n" + sb.ToString() +
            "\nScore EVERY job_id listed above. Output JSON only.";

        var resp = await _ai.CompleteAsync(user, system, maxTokens: 8192, ct, task: "resume_match");
        LastRawResponse = resp.Text;
        return ParseScores(resp.Text);
    }

    private static IReadOnlyList<Score> ParseScores(string responseText)
    {
        var list = new List<Score>();
        var first = responseText.IndexOf('{');
        var last = responseText.LastIndexOf('}');
        if (first < 0 || last <= first) return list;
        var json = responseText.Substring(first, last - first + 1);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("scores", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var elt in arr.EnumerateArray())
            {
                if (elt.ValueKind != JsonValueKind.Object) continue;
                if (!elt.TryGetProperty("job_id", out var idEl) || !idEl.TryGetInt32(out var id)) continue;
                if (!elt.TryGetProperty("score", out var scEl) || !scEl.TryGetInt32(out var sc)) continue;
                var reason = StrOrNull(elt, "reason") ?? "";
                list.Add(new Score { JobId = id, Value = Math.Clamp(sc, 0, 100), Reason = reason });
            }
        }
        catch { /* swallow */ }
        return list;
    }

    private static string? StrOrNull(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IEnumerable<IReadOnlyList<T>> Chunk<T>(IReadOnlyList<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.Skip(i).Take(size).ToList();
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n);

    private static string NormalizeWhitespace(string s)
    {
        var lines = s.Split('\n');
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var t = System.Text.RegularExpressions.Regex.Replace(line, @"[ \t]+", " ").Trim();
            if (t.Length > 0) sb.AppendLine(t);
        }
        return sb.ToString().Trim();
    }

    private sealed class Score
    {
        public int JobId { get; set; }
        public int Value { get; set; }
        public string Reason { get; set; } = "";
    }
}
