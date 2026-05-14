using System;
using Jobnet.Services.Discovery;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class DiscoverCommand : ICliCommand
{
    public string Name => "discover";
    public string Description => "Run company discovery via the configured search engine (Brave or Google CSE).  Flags: --pages <n> (default 1)";

    public int Run(string[] args, IServiceProvider services)
    {
        var pages = 1;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--pages" && int.TryParse(args[i + 1], out var n)) pages = Math.Max(1, n);

        var service = services.GetRequiredService<IDiscoveryService>();
        Console.WriteLine($"Running discovery (max {pages} page(s) per term)...");

        DiscoveryReport report;
        try
        {
            report = service.RunAsync(pages).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"Queries issued:         {report.QueriesIssued}");
        Console.WriteLine($"Results examined:       {report.ResultsExamined}");
        Console.WriteLine($"Results skipped (filter): {report.ResultsSkippedFiltered}");
        Console.WriteLine($"Companies added:        {report.CompaniesAdded}");
        Console.WriteLine($"Companies already in DB:  {report.CompaniesSkippedExisting}");

        if (report.AddedDomains.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("New companies:");
            foreach (var d in report.AddedDomains)
                Console.WriteLine($"  + {d}");
        }

        if (report.Errors.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Errors:");
            foreach (var e in report.Errors)
                Console.WriteLine($"  ! {e}");
        }

        return report.Errors.Count == 0 ? 0 : 1;
    }
}
