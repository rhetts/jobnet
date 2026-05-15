using System;
using Jobnet.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class PruneUrlsCommand : ICliCommand
{
    public string Name => "prune-urls";
    public string Description => "Delete cached URLs that haven't yielded jobs in N days.  Usage: prune-urls [--days 30]";

    public int Run(string[] args, IServiceProvider services)
    {
        var days = 30;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--days" && int.TryParse(args[i + 1], out var n)) days = Math.Max(1, n);

        var urls = services.GetRequiredService<ICompanyUrlsRepository>();
        var deleted = urls.DeleteStale(notYieldedDays: days);
        Console.WriteLine($"Pruned {deleted} URL{(deleted == 1 ? "" : "s")} not yielding jobs in last {days} days.");
        return 0;
    }
}
