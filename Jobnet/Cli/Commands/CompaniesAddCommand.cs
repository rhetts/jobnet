using System;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class CompaniesAddCommand : ICliCommand
{
    public string Name => "companies-add";
    public string Description => "Add a company.  Usage: companies-add <name> <domain> [--city X] [--careers URL]";

    public int Run(string[] args, IServiceProvider services)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: companies-add <name> <domain> [--city X] [--careers URL]");
            return 2;
        }

        var name = args[0];
        var domain = args[1];
        var city = ParseArg(args, "--city");
        var careers = ParseArg(args, "--careers");

        var repo = services.GetRequiredService<ICompanyRepository>();
        if (repo.GetByDomain(domain) is not null)
        {
            Console.WriteLine($"Company already exists for domain: {domain}");
            return 1;
        }

        var company = new Company
        {
            Id = 0,
            Name = name,
            Domain = domain,
            CareersUrl = careers,
            City = city,
            DateDiscovered = DateTime.UtcNow,
        };
        var id = repo.Insert(company);
        Console.WriteLine($"Inserted company id={id}: {name} ({domain})");
        return 0;
    }

    private static string? ParseArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}
