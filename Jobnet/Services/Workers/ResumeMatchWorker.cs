using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Data.Repositories;
using Jobnet.Services.Resume;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Services.Workers;

/// <summary>Pulls 'resume_match' queue rows in batches and scores them in one AI call via
/// <see cref="IResumeMatcher.MatchSubsetAsync"/>. Overrides the default per-item loop because
/// resume-matching is far cheaper batched (one AI call per N jobs vs N calls).</summary>
public sealed class ResumeMatchWorker
{
    private readonly IServiceScopeFactory _scopes;

    public ResumeMatchWorker(IServiceScopeFactory scopes) { _scopes = scopes; }

    public async Task RunAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(8), ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            var hadWork = false;
            try
            {
                using var scope = _scopes.CreateScope();
                var sp = scope.ServiceProvider;
                var config = sp.GetRequiredService<IConfigRepository>();

                var enabled = string.Equals(
                    config.GetOrDefault("worker.resume_match.enabled", "true"),
                    "true", StringComparison.OrdinalIgnoreCase);

                if (enabled)
                {
                    hadWork = await RunOneCycleAsync(sp, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[resume-match-worker] cycle failed: {ex.GetType().Name}: {ex.Message}");
            }

            var pollSeconds = ReadPoll();
            var delay = hadWork ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(pollSeconds);
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task<bool> RunOneCycleAsync(IServiceProvider sp, CancellationToken ct)
    {
        var config = sp.GetRequiredService<IConfigRepository>();
        var resume = sp.GetRequiredService<IResumeMatcher>();
        // Skip the cycle if the resume isn't uploaded — no point dequeueing rows we'd just fail.
        // The queue rows stay 'pending' so they resume processing automatically as soon as the
        // user uploads a resume.
        if (string.IsNullOrWhiteSpace(resume.GetStoredResume())) return false;

        var batchSize = ReadInt(config, "worker.resume_match.batch_size", 10);
        var maxAttempts = ReadInt(config, "worker.max_attempts", 3);
        var queue = sp.GetRequiredService<IJobProcessingQueueRepository>();
        var jobsRepo = sp.GetRequiredService<IJobRepository>();

        var batch = queue.ClaimNext(JobProcessingTaskTypes.ResumeMatch, batchSize);
        if (batch.Count == 0) return false;

        var jobIds = batch.Select(b => b.EntityId).ToList();
        var jobs = jobsRepo.GetByIds(jobIds);
        // Some jobs may have been deleted between enqueue and dequeue. Mark those queue rows
        // failed individually so the rest of the batch can proceed.
        var jobsById = jobs.ToDictionary(j => j.Id);
        var missingItems = batch.Where(b => !jobsById.ContainsKey(b.EntityId)).ToList();
        foreach (var miss in missingItems)
            queue.MarkFailed(miss.QueueId, $"Job {miss.EntityId} no longer exists.", maxAttempts);
        var liveItems = batch.Where(b => jobsById.ContainsKey(b.EntityId)).ToList();
        if (liveItems.Count == 0) return true;

        var liveJobs = liveItems.Select(b => jobsById[b.EntityId]).ToList();
        var outcomes = await resume.MatchSubsetAsync(liveJobs, ct);

        var notifier = sp.GetService<IEntityChangeNotifier>();
        foreach (var item in liveItems)
        {
            if (outcomes.TryGetValue(item.EntityId, out var o) && o.Success)
            {
                queue.MarkCompleted(item.QueueId);
                // Per-row notification so the UI can refresh just this card without waiting
                // for the full batch — important because resume-match batches of 10 take
                // multiple seconds and the user expects the score to land as soon as it's known.
                try { notifier?.Notify(EntityChangeKinds.Job, item.EntityId, EntityChangeKinds.ResumeMatch); } catch { }
            }
            else
                queue.MarkFailed(item.QueueId,
                    o?.Error ?? "Resume matcher returned no score for this job.",
                    maxAttempts);
        }
        return true;
    }

    private int ReadPoll()
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var c = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
            return ReadInt(c, "worker.resume_match.poll_seconds", 45);
        }
        catch { return 45; }
    }

    private static int ReadInt(IConfigRepository config, string key, int fallback)
    {
        var raw = config.GetOrDefault(key, "");
        return int.TryParse(raw, out var n) && n > 0 ? n : fallback;
    }
}
