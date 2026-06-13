using System;
using Jobnet.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

/// <summary>
/// Manually set a company's ATS type + slug, bypassing <c>detect-ats</c>. Needed when the
/// detector captures a user-facing language path (e.g. Workday's <c>/en-US</c>) instead of
/// the canonical API site name (e.g. <c>External</c>), and for one-off corrections.
/// </summary>
public sealed class CompaniesSetAtsCommand : ICliCommand
{
    public string Name => "companies-set-ats";
    public string Description =>
        "Set ATS info manually. Usage: companies-set-ats <domain> <ats_type> <ats_slug> [--careers-url <url>]";

    public int Run(string[] args, IServiceProvider services)
    {
        if (args.Length < 3) { Console.WriteLine(Description); return 2; }

        var domain  = args[0];
        var atsType = args[1];
        var atsSlug = args[2];
        string? careersUrl = null;
        for (var i = 3; i < args.Length - 1; i++)
            if (args[i] == "--careers-url") careersUrl = args[i + 1];

        var repo = services.GetRequiredService<ICompanyRepository>();
        var c = repo.GetByDomain(domain);
        if (c is null) { Console.WriteLine($"No company with domain '{domain}'"); return 1; }

        repo.SetAtsInfo(c.Id, atsType, atsSlug, careersUrl);
        Console.WriteLine($"Updated {c.Name} ({c.Domain}): ats={atsType}, slug={atsSlug}"
                          + (careersUrl is null ? "" : $", careers_url={careersUrl}"));
        return 0;
    }
}
