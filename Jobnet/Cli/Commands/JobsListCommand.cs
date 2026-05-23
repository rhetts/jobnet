using System;
using System.Linq;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Jobnet.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class JobsListCommand : ICliCommand
{
    public string Name => "jobs-list";
    public string Description => "List jobs.  Flags: --company <domain>, --show-removed, --limit <n>";

    public int Run(string[] args, IServiceProvider services)
    {
        var companyDomain = ParseArg(args, "--company");
        var showRemoved = args.Contains("--show-removed");
        var limit = int.TryParse(ParseArg(args, "--limit") ?? "0", out var n) ? n : 0;

        var companyRepo = services.GetRequiredService<ICompanyRepository>();
        var jobRepo = services.GetRequiredService<IJobRepository>();
        var dataService = services.GetRequiredService<IJobDataService>();

        var companies = companyRepo.GetAll().ToDictionary(c => c.Id, c => c.Name);

        System.Collections.Generic.IEnumerable<Job> jobs;
        if (companyDomain is not null)
        {
            var company = companyRepo.GetByDomain(companyDomain);
            if (company is null) { Console.WriteLine($"No company with domain: {companyDomain}"); return 1; }
            jobs = jobRepo.GetByCompany(company.Id, showRemoved);
        }
        else
        {
            jobs = jobRepo.GetAll(showRemoved);
        }

        var list = jobs
            .Select(j => new { Job = j, Score = dataService.ScoreJob(j) })
            .OrderByDescending(x => x.Job.ResumeMatchScore ?? -1)
            .ThenByDescending(x => x.Score)
            .ThenByDescending(x => x.Job.DateFirstSeen)
            .ToList();

        if (limit > 0) list = list.Take(limit).ToList();

        if (list.Count == 0)
        {
            Console.WriteLine("(no jobs)");
            return 0;
        }

        Console.WriteLine($"{"Score",-5}  {"Match",-5}  {"Title",-36}  {"Company",-20}  {"Remote",-7}  {"Age",-7}  Status");
        Console.WriteLine(new string('-', 110));
        foreach (var x in list)
        {
            var j = x.Job;
            companies.TryGetValue(j.CompanyId, out var companyName);
            var status = j.IsActive ? "active" : "removed";
            var age = (int)Math.Floor((DateTime.UtcNow - j.DateFirstSeen).TotalDays) + "d";
            var match = j.ResumeMatchScore?.ToString() ?? "";
            Console.WriteLine($"{x.Score,-5}  {match,-5}  {Trunc(j.Title, 36),-36}  {Trunc(companyName ?? $"#{j.CompanyId}", 20),-20}  {Trunc(j.RemoteType ?? "", 7),-7}  {age,-7}  {status}");
        }
        Console.WriteLine();
        Console.WriteLine($"{list.Count} job{(list.Count == 1 ? "" : "s")} shown.");
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
