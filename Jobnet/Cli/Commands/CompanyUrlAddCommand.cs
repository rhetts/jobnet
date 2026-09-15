using System;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

/// <summary>
/// Manually seed a cached URL for a company. Needed when the auto-discovered URL for a company
/// is wrong/stale (e.g. 404ing) and there's otherwise no way to get the correct one into the
/// cache short of waiting for the 30-day stale-URL prune — this lets the right URL take priority
/// on the very next refresh instead.
/// </summary>
public sealed class CompanyUrlAddCommand : ICliCommand
{
    public string Name => "company-url-add";
    public string Description => "Seed (or remove) a cached URL for a company. Usage: company-url-add <domain> <url> [kind]  (kind: careers_root|job_list|department|job_detail, default job_list)  |  company-url-add <domain> <url> --delete";

    public int Run(string[] args, IServiceProvider services)
    {
        if (args.Length < 2) { Console.WriteLine(Description); return 2; }

        var domain = args[0];
        var url = args[1];

        var companies = services.GetRequiredService<ICompanyRepository>();
        var urls = services.GetRequiredService<ICompanyUrlsRepository>();
        var c = companies.GetByDomain(domain);
        if (c is null) { Console.WriteLine($"No company with domain '{domain}'"); return 1; }

        if (args.Length > 2 && args[2] == "--delete")
        {
            urls.Delete(c.Id, url);
            Console.WriteLine($"Deleted cached URL for {c.Name} ({c.Domain}): {url}");
            return 0;
        }

        var kind = args.Length > 2 ? args[2] : UrlKind.JobList;
        urls.Upsert(c.Id, url, kind, discoveredVia: "manual");
        Console.WriteLine($"Cached {kind} URL for {c.Name} ({c.Domain}): {url}");
        return 0;
    }
}
