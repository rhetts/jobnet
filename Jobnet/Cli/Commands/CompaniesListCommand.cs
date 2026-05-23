using System;
using System.Linq;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class CompaniesListCommand : ICliCommand
{
    public string Name => "companies-list";
    public string Description => "List companies in the database.  Flags: --ats <type>, --limit <n>";

    public int Run(string[] args, IServiceProvider services)
    {
        var atsFilter = ParseArg(args, "--ats");
        var limit = int.TryParse(ParseArg(args, "--limit") ?? "0", out var n) ? n : 0;

        var repo = services.GetRequiredService<ICompanyRepository>();
        var jobs = services.GetRequiredService<IJobRepository>();
        var counts = jobs.GetActiveCountsByCompany();

        var all = repo.GetAll();
        var filtered = atsFilter is null ? all : all.Where(c => c.AtsType == atsFilter);
        if (limit > 0) filtered = filtered.Take(limit);
        var list = filtered.ToList();

        if (list.Count == 0)
        {
            Console.WriteLine("(no companies)");
            return 0;
        }

        Console.WriteLine($"{"ID",-4}  {"Name",-22}  {"Domain",-22}  {"City",-12}  {"ATS",-10}  {"Int",-3}  Jobs");
        Console.WriteLine(new string('-', 90));
        foreach (var c in list)
        {
            counts.TryGetValue(c.Id, out var jobCount);
            var interest = c.InterestLevel switch
            {
                InterestLevel.Approved => "+",
                InterestLevel.NotInteresting => "-",
                _ => " "
            };
            Console.WriteLine($"{c.Id,-4}  {Trunc(c.Name, 22),-22}  {Trunc(c.Domain, 22),-22}  {Trunc(c.City ?? "", 12),-12}  {Trunc(c.AtsType ?? "", 10),-10}  {interest,-3}  {jobCount}");
        }
        Console.WriteLine();
        Console.WriteLine($"{list.Count} compan{(list.Count == 1 ? "y" : "ies")} shown.");
        return 0;
    }

    private static string? ParseArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private static string Trunc(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max - 1) + "…";
}
