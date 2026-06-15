using System;
using System.Linq;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

/// <summary>
/// Text version of the Stats window — companies, jobs, queue backlog, parser-system
/// breakdown. Cheap (pure repo reads) so it's safe to script / cron.
/// </summary>
public sealed class StatsCommand : ICliCommand
{
    public string Name => "stats";
    public string Description => "Top-level counts: companies, jobs, queue backlog, extraction-system breakdown.";

    public int Run(string[] args, IServiceProvider services)
    {
        var companies = services.GetRequiredService<ICompanyRepository>().GetAll();
        var jobs = services.GetRequiredService<IJobRepository>().GetAll(includeRemoved: true);
        var queue = services.GetRequiredService<IJobProcessingQueueRepository>().GetStats();

        var active = companies.Where(c => c.IsActive).ToList();
        var activeJobs = jobs.Count(j => j.IsActive);

        Console.WriteLine("=== Companies ===");
        Console.WriteLine($"  active      : {active.Count}");
        Console.WriteLine($"  inactive    : {companies.Count - active.Count}");
        Console.WriteLine($"  total       : {companies.Count}");
        Console.WriteLine();

        Console.WriteLine("=== Jobs ===");
        Console.WriteLine($"  active           : {activeJobs}");
        Console.WriteLine($"  removed          : {jobs.Count - activeJobs}");
        Console.WriteLine($"  total            : {jobs.Count}");
        Console.WriteLine($"  with summary     : {jobs.Count(j => j.IsActive && !string.IsNullOrWhiteSpace(j.Summary))}");
        Console.WriteLine($"  with resume score: {jobs.Count(j => j.IsActive && j.ResumeMatchScore.HasValue)}");
        Console.WriteLine();

        Console.WriteLine("=== Background queue ===");
        if (queue.Count == 0) Console.WriteLine("  (empty)");
        else
        {
            foreach (var s in queue)
                Console.WriteLine($"  {s.TaskType,-15} {s.Status,-10} {s.Count}");
        }
        Console.WriteLine();

        Console.WriteLine("=== Companies by extraction system ===");
        var grouped = active
            .Select(Classify)
            .GroupBy(s => s)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToList();
        foreach (var g in grouped)
            Console.WriteLine($"  {g.Key,-30} {g.Count()}");
        return 0;
    }

    private static string Classify(Company c)
    {
        if (!string.IsNullOrEmpty(c.AtsType) && !string.IsNullOrEmpty(c.AtsSlug))
            return $"native: {c.AtsType}";
        if (!string.IsNullOrWhiteSpace(c.LastCompanyParser))
            return $"hand-written: {c.LastCompanyParser}";
        if (!string.IsNullOrWhiteSpace(c.ParserStrategy) && !c.ParserStrategyDisabled)
            return "cached selectors";
        if (c.DateLastScan is null)
            return "never scanned";
        return "AI extract";
    }
}
