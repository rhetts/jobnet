using System;
using Jobnet.Data.Repositories;
using Jobnet.Services.Location;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class PruneOutOfAreaCommand : ICliCommand
{
    public string Name => "prune-out-of-area";
    public string Description => "Mark active jobs as removed when their location is clearly not Vancouver-area. Usage: prune-out-of-area [--dry-run]";

    public int Run(string[] args, IServiceProvider services)
    {
        var jobs = services.GetRequiredService<IJobRepository>();
        var dryRun = Array.IndexOf(args, "--dry-run") >= 0;

        var rows = jobs.GetActiveLocations();
        var doomed = 0;
        var now = DateTime.UtcNow;
        foreach (var (id, location) in rows)
        {
            if (LocationMatcher.IsVancouverArea(location)) continue;
            doomed++;
            if (dryRun)
                Console.WriteLine($"  would remove job {id,5}  location: {location}");
            else
                jobs.MarkRemoved(id, now);
        }

        Console.WriteLine();
        if (dryRun)
            Console.WriteLine($"Dry run: {doomed} job(s) would be marked removed.");
        else
            Console.WriteLine($"Marked {doomed} job(s) as removed (out of area).");
        return 0;
    }
}
