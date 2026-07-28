using System;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Jobnet.Services.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class PruneUrlsCommand : ICliCommand
{
    public string Name => "prune-urls";
    public string Description => "Delete cached URLs: stale ones, and any the filter rules block.  "
                               + "Usage: prune-urls [--days 30] [--blocked-only] [--dry-run]";

    public int Run(string[] args, IServiceProvider services)
    {
        var days = 30;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--days" && int.TryParse(args[i + 1], out var n)) days = Math.Max(1, n);

        var blockedOnly = Array.IndexOf(args, "--blocked-only") >= 0;
        var dryRun      = Array.IndexOf(args, "--dry-run") >= 0;

        var urls = services.GetRequiredService<ICompanyUrlsRepository>();
        var filters = services.GetRequiredService<FilterRuleProvider>().Current;

        // Blocked URLs first. JobRefresher does this at the top of every batch, but that only
        // helps if a refresh is actually run — this makes the purge available on demand right
        // after editing a rule.
        if (dryRun)
        {
            var wouldPurge = 0;
            foreach (var u in urls.GetAll())
                if (filters.IsUrlBlocked(u.Url, FilterScope.Crawl)) wouldPurge++;
            Console.WriteLine($"[dry run] {wouldPurge} URL(s) match a filter rule and would be deleted.");
            if (!blockedOnly)
                Console.WriteLine($"[dry run] stale pruning (--days {days}) not simulated; re-run without --dry-run.");
            return 0;
        }

        var blocked = urls.DeleteWhere(u => filters.IsUrlBlocked(u, FilterScope.Crawl));
        Console.WriteLine($"Deleted {blocked} URL{(blocked == 1 ? "" : "s")} matching a filter rule.");

        if (!blockedOnly)
        {
            var stale = urls.DeleteStale(notYieldedDays: days);
            Console.WriteLine($"Pruned {stale} URL{(stale == 1 ? "" : "s")} not yielding jobs in last {days} days.");
        }

        filters.FlushHits();
        return 0;
    }
}
