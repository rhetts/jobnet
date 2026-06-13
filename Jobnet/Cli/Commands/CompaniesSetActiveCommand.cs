using System;
using System.Linq;
using Jobnet.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

/// <summary>
/// Toggle a company's <c>is_active</c> flag. Inactive companies are skipped by
/// <c>refresh-jobs</c> but their historical postings are preserved, so the user
/// can still browse them. The reverse is just <c>--activate</c>.
/// </summary>
public sealed class CompaniesSetActiveCommand : ICliCommand
{
    public string Name => "companies-set-active";
    public string Description =>
        "Activate/deactivate companies. Usage: companies-set-active --deactivate <domain> [<domain>...]  |  --activate <domain>...  |  --id <n> --deactivate";

    public int Run(string[] args, IServiceProvider services)
    {
        if (args.Length < 2) { Console.WriteLine(Description); return 2; }

        var repo = services.GetRequiredService<ICompanyRepository>();
        bool? active = null;
        var targets = new System.Collections.Generic.List<string>();
        int? targetId = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--activate":   active = true;  break;
                case "--deactivate": active = false; break;
                case "--id" when i + 1 < args.Length && int.TryParse(args[i + 1], out var n):
                    targetId = n; i++; break;
                default:
                    targets.Add(args[i]);
                    break;
            }
        }

        if (active is null)
        {
            Console.WriteLine("Must specify --activate or --deactivate.");
            return 2;
        }

        var changed = 0;
        if (targetId is { } id)
        {
            var c = repo.GetById(id);
            if (c is null) { Console.WriteLine($"No company with id {id}"); return 1; }
            repo.SetActive(id, active.Value);
            Console.WriteLine($"  {(active.Value ? "Activated" : "Deactivated")} id {id}: {c.Name} ({c.Domain})");
            changed++;
        }

        foreach (var domain in targets)
        {
            var c = repo.GetByDomain(domain);
            if (c is null) { Console.WriteLine($"  (skip) no company with domain '{domain}'"); continue; }
            repo.SetActive(c.Id, active.Value);
            Console.WriteLine($"  {(active.Value ? "Activated" : "Deactivated")} id {c.Id}: {c.Name} ({c.Domain})");
            changed++;
        }

        Console.WriteLine();
        Console.WriteLine($"Updated {changed} compan{(changed == 1 ? "y" : "ies")}.");
        return changed == 0 ? 1 : 0;
    }
}
