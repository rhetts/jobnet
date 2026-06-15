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
        "Enqueue summary + resume-match + company-profile tasks for any entity missing one. " +
        "Flags: --summary-only, --match-only, --profile-only";

    public int Run(string[] args, IServiceProvider services)
    {
        var summaryOnly = args.Contains("--summary-only");
        var matchOnly = args.Contains("--match-only");
        var profileOnly = args.Contains("--profile-only");
        // If a specific --*-only flag is set, the others are skipped. No --*-only flags = run all.
        var runSummary  = !matchOnly && !profileOnly;
        var runMatch    = !summaryOnly && !profileOnly;
        var runProfile  = !summaryOnly && !matchOnly;

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
            var n = queue.EnqueueMissing(ids, JobProcessingTaskTypes.ResumeMatch);
            Console.WriteLine($"resume_match    : {n,5} enqueued ({ids.Count - n} already in queue)");
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
