using System;
using Jobnet.Services.JobBoards;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

/// <summary>
/// Diagnostic-only entry point for <see cref="IJobBoardSource"/> — the user-facing trigger is
/// the "Include job boards" checkbox on the Refresh window's Discover Jobs step; this exists so
/// Claude sessions can exercise/verify a board source directly without going through the GUI.
/// </summary>
public sealed class HarvestJobBoardCommand : ICliCommand
{
    public string Name => "harvest-jobboard";
    public string Description => "Diagnostic: run one IJobBoardSource directly (currently only tnet_bc_jobboard).";

    public int Run(string[] args, IServiceProvider services)
    {
        var source = services.GetRequiredService<IJobBoardSource>();
        Console.WriteLine($"Ingesting via {source.Name}...");
        var r = source.IngestAsync().GetAwaiter().GetResult();
        Console.WriteLine();
        Console.WriteLine($"Pages processed:        {r.PagesProcessed}");
        Console.WriteLine($"Rows examined:          {r.RowsExamined}");
        Console.WriteLine($"Companies created:      {r.CompaniesCreated}");
        Console.WriteLine($"Jobs added:             {r.JobsAdded}");
        Console.WriteLine($"Jobs updated:           {r.JobsUpdated}");
        Console.WriteLine($"Skipped (out of area):  {r.SkippedOutOfArea}");
        Console.WriteLine($"Skipped (unmatched co): {r.SkippedUnmatchedCompany}");
        if (r.Errors.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Errors:");
            foreach (var e in r.Errors) Console.WriteLine($"  ! {e}");
        }
        return r.Errors.Count > 0 ? 1 : 0;
    }
}
