using System;
using Jobnet.Services.Classification;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class ReclassifyJobsCommand : ICliCommand
{
    public string Name => "reclassify-jobs";
    public string Description => "Re-run the heuristic classifier across all active jobs. Use after taxonomy changes.";

    public int Run(string[] args, IServiceProvider services)
    {
        var reclassifier = services.GetRequiredService<IJobReclassifier>();
        Console.WriteLine("Reclassifying all active jobs...");
        var r = reclassifier.ReclassifyAll();
        Console.WriteLine();
        Console.WriteLine($"Examined: {r.Examined}");
        Console.WriteLine($"Changed:  {r.Changed}");
        return 0;
    }
}
