using System;
using System.IO;
using System.Linq;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class SeedCompaniesCommand : ICliCommand
{
    public string Name => "seed-companies";
    public string Description =>
        "Bulk-import companies from a CSV.  Usage: seed-companies <file.csv>\n" +
        "Format: name,domain[,careers_url][,city][,ats_type][,ats_slug]  (header row optional)";

    public int Run(string[] args, IServiceProvider services)
    {
        if (args.Length < 1)
        {
            Console.WriteLine(Description);
            return 2;
        }

        var path = args[0];
        if (!File.Exists(path)) { Console.WriteLine($"File not found: {path}"); return 1; }

        var companies = services.GetRequiredService<ICompanyRepository>();
        var discoveries = services.GetRequiredService<ICompanyDiscoveryRepository>();
        var sourceName = Path.GetFileName(path);
        var added = 0; var skipped = 0; var bad = 0;

        var lines = File.ReadAllLines(path);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            if (line.StartsWith("name,", StringComparison.OrdinalIgnoreCase)) continue;  // header row

            var parts = line.Split(',').Select(s => s.Trim()).ToArray();
            if (parts.Length < 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
            {
                Console.WriteLine($"  ! skipped malformed: {line}");
                bad++;
                continue;
            }

            var name = parts[0];
            var domain = parts[1].ToLowerInvariant().Replace("https://", "").Replace("http://", "").TrimEnd('/');
            if (domain.StartsWith("www.")) domain = domain[4..];
            var careersUrl = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null;
            var city = parts.Length > 3 && parts[3].Length > 0 ? parts[3] : null;
            var atsType = parts.Length > 4 && parts[4].Length > 0 ? parts[4] : null;
            var atsSlug = parts.Length > 5 && parts[5].Length > 0 ? parts[5] : null;

            if (companies.GetByDomain(domain) is not null)
            {
                skipped++;
                continue;
            }

            var company = new Company
            {
                Id = 0,
                Name = name,
                Domain = domain,
                CareersUrl = careersUrl,
                City = city,
                AtsType = atsType,
                AtsSlug = atsSlug,
                DateDiscovered = DateTime.UtcNow,
            };
            var newId = companies.Insert(company);
            if (!string.IsNullOrEmpty(atsType))
                companies.SetAtsInfo(newId, atsType, atsSlug, careersUrl);
            discoveries.Record(newId, "seed_csv", sourceName, sourceUrl: careersUrl, runId: null);

            Console.WriteLine($"  + {name} ({domain})" + (atsType is null ? "" : $"  [{atsType}{(atsSlug is null ? "" : ":" + atsSlug)}]"));
            added++;
        }

        Console.WriteLine();
        Console.WriteLine($"Added {added}, skipped {skipped} (already in DB), {bad} malformed.");
        return bad > 0 && added == 0 ? 1 : 0;
    }
}
