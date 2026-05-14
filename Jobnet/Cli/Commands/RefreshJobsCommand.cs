using System;
using Jobnet.Data.Repositories;
using Jobnet.Services.AtsAdapters;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class RefreshJobsCommand : ICliCommand
{
    public string Name => "refresh-jobs";
    public string Description => "Refresh jobs from ATS APIs. Usage: refresh-jobs [--company <domain>]";

    public int Run(string[] args, IServiceProvider services)
    {
        var refresher = services.GetRequiredService<IJobRefresher>();
        var companies = services.GetRequiredService<ICompanyRepository>();

        string? domain = null;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--company") domain = args[i + 1];

        JobRefreshReport report;
        if (domain is not null)
        {
            var company = companies.GetByDomain(domain);
            if (company is null) { Console.WriteLine($"No company with domain '{domain}'"); return 1; }
            Console.WriteLine($"Refreshing {company.Name} ({company.Domain}) via {company.AtsType ?? "(no ATS)"}...");
            report = refresher.RefreshAsync(company).GetAwaiter().GetResult();
        }
        else
        {
            Console.WriteLine("Refreshing all companies with detected ATS...");
            report = refresher.RefreshAllAsync().GetAwaiter().GetResult();
        }

        Console.WriteLine();
        Console.WriteLine($"Companies processed:  {report.CompaniesProcessed}");
        Console.WriteLine($"Companies skipped:    {report.CompaniesSkippedNoAts} (no ATS detected)");
        Console.WriteLine($"Companies failed:     {report.CompaniesFailed}");
        Console.WriteLine($"Jobs added:           {report.JobsAdded}");
        Console.WriteLine($"Jobs updated:         {report.JobsUpdated}");
        Console.WriteLine($"Jobs marked removed:  {report.JobsRemoved}");
        if (report.Errors.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Errors:");
            foreach (var e in report.Errors) Console.WriteLine($"  ! {e}");
        }
        return report.CompaniesFailed > 0 ? 1 : 0;
    }
}
