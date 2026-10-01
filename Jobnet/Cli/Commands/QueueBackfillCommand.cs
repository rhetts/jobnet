using System;
using System.Linq;
using Jobnet.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

/// <summary>
/// Seeds <c>job_processing_queue</c> with rows for every active job that's missing a
/// summary and/or a resume_match_score. Idempotent — re-running is safe; the queue's
/// UNIQUE(job_id, task_type) absorbs existing rows.
///
/// Used once after migration 048 to enqueue the backlog, and any time the user wants
/// to force a re-scan (e.g. after a model change or prompt tweak — though the workers
/// only fill in what's missing, not regenerate). For full regeneration, manually clear
/// summary / resume_match_score columns first.
/// </summary>
public sealed class QueueBackfillCommand : ICliCommand
{
    public string Name => "queue-backfill";
    public string Description =>
        "Enqueue summary + resume-match + company-profile + classify tasks for any entity missing one. " +
        "Flags: --summary-only, --match-only, --profile-only, --classify-only, --requeue-stuck (also reset failed/" +
        "completed resume-match rows for jobs still missing a score — same as Stats > Rescore stuck)";

    public int Run(string[] args, IServiceProvider services)
    {
        var summaryOnly = args.Contains("--summary-only");
        var matchOnly = args.Contains("--match-only");
        var profileOnly = args.Contains("--profile-only");
        var classifyOnly = args.Contains("--classify-only");
        var requeueStuck = args.Contains("--requeue-stuck");
        // If a specific --*-only flag is set, the others are skipped. No --*-only flags = run all.
        var anyOnly     = summaryOnly || matchOnly || profileOnly || classifyOnly;
        var runSummary  = !anyOnly || summaryOnly;
        var runMatch    = !anyOnly || matchOnly;
        var runProfile  = !anyOnly || profileOnly;
        var runClassify = !anyOnly || classifyOnly;

        var jobs = services.GetRequiredService<IJobRepository>();
        var companies = services.GetRequiredService<ICompanyRepository>();
        var queue = services.GetRequiredService<IJobProcessingQueueRepository>();

        if (runSummary)
        {
            var ids = jobs.GetActiveIdsMissingSummary();
            var n = queue.EnqueueMissing(ids, JobProcessingTaskTypes.Summary);
            Console.WriteLine($"summary         : {n,5} enqueued ({ids.Count - n} already in queue)");
        }
        if (runMatch)
        {
            var ids = jobs.GetActiveIdsMissingResumeMatch();
            if (requeueStuck)
            {
                var reset = queue.Requeue(ids, JobProcessingTaskTypes.ResumeMatch);
                Console.WriteLine($"resume_match    : {reset,5} stuck rows reset to pending");
            }
            var n = queue.EnqueueMissing(ids, JobProcessingTaskTypes.ResumeMatch);
            Console.WriteLine($"resume_match    : {n,5} enqueued ({ids.Count - n} already in queue)");
        }
        if (runClassify)
        {
            var ids = jobs.GetActiveIdsUnclassified();
            var n = queue.EnqueueMissing(ids, JobProcessingTaskTypes.Classify);
            Console.WriteLine($"classify        : {n,5} enqueued ({ids.Count - n} already in queue)");
        }
        if (runProfile)
        {
            var ids = companies.GetActiveIdsMissingProfile();
            var n = queue.EnqueueMissing(ids, JobProcessingTaskTypes.CompanyProfile);
            Console.WriteLine($"company_profile : {n,5} enqueued ({ids.Count - n} already in queue)");
        }
        Console.WriteLine();
        Console.WriteLine("Current queue state:");
        foreach (var s in queue.GetStats())
            Console.WriteLine($"  {s.TaskType,-15} {s.Status,-10} {s.Count}");
        return 0;
    }
}
