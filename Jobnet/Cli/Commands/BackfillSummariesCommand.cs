using System;
using Jobnet.Services.Summarization;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class BackfillSummariesCommand : ICliCommand
{
    public string Name => "backfill-summaries";
    public string Description => "Generate AI summaries for active jobs that don't have one yet. Usage: backfill-summaries [--max N]";

    public int Run(string[] args, IServiceProvider services)
    {
        var summarizer = services.GetRequiredService<IJobSummarizer>();
        var max = 50;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--max" && int.TryParse(args[i + 1], out var n)) max = n;

        Console.WriteLine($"Backfilling up to {max} job summaries...");
        var report = summarizer.BackfillAsync(max).GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine($"Examined:  {report.Examined}");
        Console.WriteLine($"Generated: {report.Generated}");
        Console.WriteLine($"Skipped:   {report.Skipped}");
        Console.WriteLine($"Failed:    {report.Failed}");
        if (report.Errors.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Errors:");
            foreach (var e in report.Errors) Console.WriteLine($"  ! {e}");
        }
        return report.Failed > 0 ? 1 : 0;
    }
}
