using System;
using System.Threading.Tasks;
using Jobnet.Data.Repositories;
using Jobnet.Services.AtsDetection;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class DetectAtsCommand : ICliCommand
{
    public string Name => "detect-ats";
    public string Description => "Detect ATS for a company. Usage: detect-ats <domain> | --all | --missing";

    public int Run(string[] args, IServiceProvider services)
    {
        if (args.Length < 1)
        {
            Console.WriteLine(Description);
            return 2;
        }

        var companies = services.GetRequiredService<ICompanyRepository>();
        var detector  = services.GetRequiredService<IAtsDetector>();

        if (args[0] == "--all" || args[0] == "--missing")
        {
            var onlyMissing = args[0] == "--missing";
            var all = companies.GetAll();
            var processed = 0;
            var hits = 0;
            foreach (var c in all)
            {
                if (onlyMissing && !string.IsNullOrEmpty(c.AtsType)) continue;
                Console.WriteLine($"[{c.Name}] {c.Domain}");
                var result = detector.DetectAsync(c).GetAwaiter().GetResult();
                processed++;
                if (result.AtsType is not null)
                {
                    hits++;
                    companies.SetAtsInfo(c.Id, result.AtsType, result.AtsSlug, result.ResolvedCareersUrl);
                    Console.WriteLine($"  → {result.AtsType}:{result.AtsSlug}  (via {result.Source})  {result.ResolvedCareersUrl}");
                }
                else
                {
                    Console.WriteLine($"  → no ATS detected. {result.Notes}");
                }
            }
            Console.WriteLine();
            Console.WriteLine($"Processed {processed} compan{(processed == 1 ? "y" : "ies")}, {hits} ATS hits.");
            return 0;
        }

        var domain = args[0];
        var company = companies.GetByDomain(domain);
        if (company is null) { Console.WriteLine($"No company with domain '{domain}'"); return 1; }

        var r = detector.DetectAsync(company).GetAwaiter().GetResult();
        Console.WriteLine($"Domain:          {company.Domain}");
        Console.WriteLine($"Source:          {r.Source}");
        Console.WriteLine($"ATS type:        {r.AtsType ?? "(none)"}");
        Console.WriteLine($"ATS slug:        {r.AtsSlug ?? "(none)"}");
        Console.WriteLine($"Resolved URL:    {r.ResolvedCareersUrl ?? "(none)"}");
        if (!string.IsNullOrEmpty(r.Notes)) Console.WriteLine($"Notes:           {r.Notes}");

        if (r.AtsType is not null)
        {
            companies.SetAtsInfo(company.Id, r.AtsType, r.AtsSlug, r.ResolvedCareersUrl);
            Console.WriteLine();
            Console.WriteLine("Persisted to DB.");
        }
        return 0;
    }
}
