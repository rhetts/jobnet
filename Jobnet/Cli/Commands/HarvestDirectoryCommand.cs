using System;
using Jobnet.Services.Discovery;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class HarvestDirectoryCommand : ICliCommand
{
    public string Name => "harvest-directory";
    public string Description => "Pull companies from a directory or VC-portfolio page via Playwright + AI. Usage: harvest-directory <url>";

    public int Run(string[] args, IServiceProvider services)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: harvest-directory <url>");
            return 1;
        }
        var url = args[0];
        var harvester = services.GetRequiredService<ICompanyDirectoryHarvester>();
        Console.WriteLine($"Harvesting companies from {url}...");
        var r = harvester.HarvestAsync(url).GetAwaiter().GetResult();
        Console.WriteLine();
        Console.WriteLine($"Page text chars:    {r.PageTextChars}");
        Console.WriteLine($"Anchors found:      {r.AnchorsFound}");
        Console.WriteLine($"Candidates found:   {r.CandidatesFound}");
        Console.WriteLine($"Companies added:    {r.CompaniesAdded}");
        Console.WriteLine($"Skipped (existing): {r.CompaniesSkippedExisting}");
        Console.WriteLine($"Skipped (filtered): {r.CompaniesSkippedFiltered}");
        if (r.Errors.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Errors:");
            foreach (var e in r.Errors) Console.WriteLine($"  ! {e}");
        }
        if (r.CandidatesFound == 0 && !string.IsNullOrEmpty(r.RawAiResponse))
        {
            Console.WriteLine();
            Console.WriteLine($"Raw AI response ({r.RawAiResponse.Length} chars):");
            Console.WriteLine(r.RawAiResponse);
        }
        return r.Errors.Count > 0 ? 1 : 0;
    }
}
