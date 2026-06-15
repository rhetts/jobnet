using System;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Data.Repositories;
using Jobnet.Services.Summarization;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Services.Workers;

/// <summary>Pulls 'summary' queue rows and calls <see cref="IJobSummarizer.SummarizeAsync"/>
/// once per row. No batching — JobSummarizer hits the AI provider per job because each
/// posting needs its own fetched-page text and prompt context.</summary>
public sealed class SummaryWorker : QueueWorker
{
    public SummaryWorker(IServiceScopeFactory scopes) : base(scopes) { }

    protected override string WorkerName => "summary";
    protected override string TaskType => JobProcessingTaskTypes.Summary;
    protected override int DefaultPollSeconds => 30;
    protected override int DefaultBatchSize => 5;

    protected override async Task<bool> ProcessOneAsync(IServiceProvider scoped, DequeuedItem item, CancellationToken ct)
    {
        var jobs = scoped.GetRequiredService<IJobRepository>();
        var job = jobs.GetByIds(new[] { item.EntityId });
        if (job.Count == 0)
        {
            // Job got deleted/removed between enqueue and dequeue — mark the row failed so
            // it stops cycling. The error message is the breadcrumb; alternative would be
            // to silently drop, but that hides the inconsistency.
            throw new InvalidOperationException($"Job {item.EntityId} no longer exists.");
        }
        var summarizer = scoped.GetRequiredService<IJobSummarizer>();
        var result = await summarizer.SummarizeAsync(job[0], ct);
        if (result.Success) return true;
        if (result.Skipped)
        {
            // "Skipped" = nothing to summarise (no URL, no description). Don't keep retrying
            // — mark as success so the queue row resolves; the job just won't have a summary.
            return true;
        }
        throw new InvalidOperationException(result.Error ?? "Summarisation failed without error message.");
    }

    protected override void NotifyAfterSuccess(IServiceProvider scoped, DequeuedItem item)
    {
        scoped.GetService<IEntityChangeNotifier>()?
              .Notify(EntityChangeKinds.Job, item.EntityId, EntityChangeKinds.Summary);
    }
}
