using System;
using System.Linq;
using Jobnet.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class CompanyUrlsCommand : ICliCommand
{
    public string Name => "company-urls";
    public string Description => "Show cached URLs for a company.  Usage: company-urls <domain> [--kind X]";

    public int Run(string[] args, IServiceProvider services)
    {
        if (args.Length < 1)
        {
            Console.WriteLine(Description);
            return 2;
        }

        var companies = services.GetRequiredService<ICompanyRepository>();
        var urls = services.GetRequiredService<ICompanyUrlsRepository>();
        var domain = args[0];

        string? kindFilter = null;
        for (var i = 1; i < args.Length - 1; i++)
            if (args[i] == "--kind") kindFilter = args[i + 1];

        var c = companies.GetByDomain(domain);
        if (c is null) { Console.WriteLine($"No company with domain '{domain}'"); return 1; }

        var list = kindFilter is null ? urls.GetByCompany(c.Id) : urls.GetByCompanyAndKind(c.Id, kindFilter);

        if (list.Count == 0)
        {
            Console.WriteLine($"(no URLs cached for {c.Name})");
            return 0;
        }

        Console.WriteLine($"Cached URLs for {c.Name} ({list.Count} total):");
        Console.WriteLine($"{"Kind",-14}  {"Fails",5}  {"Last yielded",-21}  URL  (label)");
        Console.WriteLine(new string('-', 100));
        foreach (var u in list.OrderBy(u => u.Kind).ThenBy(u => u.Url))
        {
            var yielded = u.LastYielded.HasValue ? u.LastYielded.Value.ToString("yyyy-MM-dd HH:mm") : "(never)";
            var label = string.IsNullOrEmpty(u.Label) ? "" : $"  [{u.Label}]";
            Console.WriteLine($"{u.Kind,-14}  {u.FailCount,5}  {yielded,-21}  {u.Url}{label}");
        }
        return 0;
    }
}
