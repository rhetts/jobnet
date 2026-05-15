using System;
using Jobnet.Services.AtsAdapters;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class ParsePageCommand : ICliCommand
{
    public string Name => "parse-page";
    public string Description => "Render a careers page with Playwright + AI-extract jobs. Usage: parse-page <url>";

    public int Run(string[] args, IServiceProvider services)
    {
        if (args.Length < 1)
        {
            Console.WriteLine(Description);
            return 2;
        }

        var url = args[0];
        var extractor = services.GetRequiredService<AiExtractedJobSource>();
        Console.WriteLine($"Rendering and parsing {url}...");

        try
        {
            var jobs = extractor.FetchAsync(url).GetAwaiter().GetResult();
            if (jobs.Count == 0) { Console.WriteLine("(no jobs extracted)"); return 0; }

            Console.WriteLine($"Extracted {jobs.Count} job{(jobs.Count == 1 ? "" : "s")}:");
            foreach (var j in jobs)
            {
                Console.WriteLine($"  • {j.Title}");
                if (!string.IsNullOrEmpty(j.Location))    Console.WriteLine($"      location:   {j.Location}");
                if (!string.IsNullOrEmpty(j.Department))  Console.WriteLine($"      department: {j.Department}");
                if (!string.IsNullOrEmpty(j.RemoteType))  Console.WriteLine($"      remote:     {j.RemoteType}");
                if (!string.IsNullOrEmpty(j.Url) && j.Url != url) Console.WriteLine($"      url:        {j.Url}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED: {ex.Message}");
            return 1;
        }
    }
}
