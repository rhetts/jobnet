using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Data.Repositories;
using Jobnet.Services.Classification;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Services.Workers;

/// <summary>Pulls 'classify' queue rows — new jobs whose title the heuristic couldn't place —
/// and runs the AI classifier on each, writing back the level and areas. This used to happen
/// inline in the refresh loop (<see cref="CompositeClassifier"/>), blocking the scan on the AI
/// once per unplaceable title on every refresh.</summary>
public sealed class ClassifyWorker : QueueWorker
{
    public ClassifyWorker(IServiceScopeFactory scopes) : base(scopes) { }

    protected override string WorkerName => "classify";
    protected override string TaskType => JobProcessingTaskTypes.Classify;
    protected override int DefaultPollSeconds => 30;
    protected override int DefaultBatchSize => 10;

    protected override Task<bool> ProcessOneAsync(IServiceProvider scoped, DequeuedItem item, CancellationToken ct)
    {
        var jobs = scoped.GetRequiredService<IJobRepository>();
        var found = jobs.GetByIds(new[] { item.EntityId });
        if (found.Count == 0)
            throw new InvalidOperationException($"Job {item.EntityId} no longer exists.");
        var job = found[0];

        // Classified meanwhile (user edit, reclassify run, or the heuristic after a title
        // change) — nothing to do.
        if (job.LevelId.HasValue || job.AreaIds.Count > 0) return Task.FromResult(true);

        var result = scoped.GetRequiredService<AiFallbackClassifier>().Classify(job.Title);

        // AiFallbackClassifier reports provider failures as Source="none" rather than throwing;
        // surface those so the row is retried instead of resolving with no classification.
        if (result.Source == "none" && result.Reason is not null && result.Reason.StartsWith("AI call failed"))
            throw new InvalidOperationException(result.Reason);

        if (result.LevelId.HasValue) jobs.SetLevel(job.Id, result.LevelId);
        if (result.Areas.Count > 0)
            scoped.GetRequiredService<IAreaRepository>().SetAreasForJob(job.Id, result.Areas.Select(a => a.Id).ToList());
        // An AI answer of "no level, no areas" is still a completed classification — don't retry.
        return Task.FromResult(true);
    }

    protected override void NotifyAfterSuccess(IServiceProvider scoped, DequeuedItem item)
    {
        scoped.GetService<IEntityChangeNotifier>()?
              .Notify(EntityChangeKinds.Job, item.EntityId, EntityChangeKinds.Classification);
    }
}
