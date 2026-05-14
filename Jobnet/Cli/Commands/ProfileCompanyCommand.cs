using System;
using Jobnet.Data.Repositories;
using Jobnet.Services.Profiling;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class ProfileCompanyCommand : ICliCommand
{
    public string Name => "profile-company";
    public string Description => "Generate a Claude Haiku profile for a company. Usage: profile-company <domain> | --all-missing";

    public int Run(string[] args, IServiceProvider services)
    {
        if (args.Length < 1)
        {
            Console.WriteLine(Description);
            return 2;
        }

        var companies = services.GetRequiredService<ICompanyRepository>();
        var profiler  = services.GetRequiredService<ICompanyProfiler>();

        if (args[0] == "--all-missing")
        {
            var all = companies.GetAll();
            var processed = 0;
            var ok = 0;
            foreach (var c in all)
            {
                var existing = companies.GetProfile(c.Id);
                if (existing is not null && !string.IsNullOrWhiteSpace(existing.Summary)) continue;

                Console.WriteLine($"[{c.Name}] {c.Domain}");
                var result = profiler.GenerateAndPersistAsync(c).GetAwaiter().GetResult();
                processed++;
                if (result.Success)
                {
                    ok++;
                    Console.WriteLine($"  ✓ {Trunc(result.Profile?.Summary, 100)}");
                }
                else
                {
                    Console.WriteLine($"  ✗ {result.Error}");
                }
            }
            Console.WriteLine($"\nProcessed {processed} compan{(processed == 1 ? "y" : "ies")}, {ok} profiled.");
            return ok == 0 && processed > 0 ? 1 : 0;
        }

        var domain = args[0];
        var company = companies.GetByDomain(domain);
        if (company is null) { Console.WriteLine($"No company with domain '{domain}'"); return 1; }

        Console.WriteLine($"Profiling {company.Name} ({company.Domain})...");
        var r = profiler.GenerateAndPersistAsync(company).GetAwaiter().GetResult();
        if (!r.Success) { Console.WriteLine($"Failed: {r.Error}"); return 1; }

        var p = r.Profile!;
        Console.WriteLine();
        Console.WriteLine($"Summary:      {p.Summary}");
        Console.WriteLine($"Products:     {string.Join(", ", p.Products)}");
        Console.WriteLine($"Industries:   {string.Join(", ", p.Industries)}");
        Console.WriteLine($"Tech signals: {string.Join(", ", p.TechSignals)}");
        Console.WriteLine($"HQ:           {p.HeadquartersHint ?? "(none)"}");
        Console.WriteLine($"Size:         {p.SizeHint ?? "(none)"}");
        Console.WriteLine($"Model:        {p.Model}  Source: {r.SourceUrl}");
        return 0;
    }

    private static string Trunc(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");
}
