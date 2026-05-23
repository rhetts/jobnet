using System;
using System.Collections.Generic;
using System.Linq;
using Jobnet.Services.Discovery.Strategies;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class DiscoverStrategyCommand : ICliCommand
{
    public string Name => "discover-strategy";
    public string Description => "Run a named discovery strategy. Usage: discover-strategy [--list] [--name \"<strategy name>\"]";

    public int Run(string[] args, IServiceProvider services)
    {
        var strategies = services.GetRequiredService<IDiscoveryStrategyProvider>().GetAll().ToList();

        if (args.Length == 0 || Array.IndexOf(args, "--list") >= 0)
        {
            Console.WriteLine("Available strategies:");
            foreach (var s in strategies) Console.WriteLine($"  - {s.Name}");
            return 0;
        }

        string? want = null;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--name") want = args[i + 1];
        if (string.IsNullOrEmpty(want))
        {
            Console.WriteLine("Usage: discover-strategy --name \"<strategy name>\"  (use --list to see options)");
            return 1;
        }

        var strategy = strategies.FirstOrDefault(s => s.Name.Equals(want, StringComparison.OrdinalIgnoreCase))
                    ?? strategies.FirstOrDefault(s => s.Name.Contains(want!, StringComparison.OrdinalIgnoreCase));
        if (strategy is null)
        {
            Console.WriteLine($"No strategy matches '{want}'. Use --list to see options.");
            return 1;
        }

        Console.WriteLine($"Running: {strategy.Name}");
        var r = strategy.RunAsync().GetAwaiter().GetResult();
        Console.WriteLine();
        Console.WriteLine($"Candidates examined: {r.CandidatesExamined}");
        Console.WriteLine($"Companies added:     {r.CompaniesAdded}");
        Console.WriteLine($"Skipped (existing):  {r.CompaniesSkippedExisting}");
        Console.WriteLine($"Skipped (filtered):  {r.CompaniesSkippedFiltered}");
        if (r.Errors.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Errors:");
            foreach (var e in r.Errors) Console.WriteLine($"  ! {e}");
        }
        return r.Errors.Count > 0 ? 1 : 0;
    }
}
