using System;
using Jobnet.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

/// <summary>
/// Manually clear a company's ats_type/ats_slug, falling it back to ATS detection + AI-extract
/// on the next refresh. Companion to <see cref="CompaniesSetAtsCommand"/> — needed when a slug
/// was guessed wrong and no working replacement is discoverable, rather than leaving a company
/// pinned to a native adapter that only ever 404s or fails to parse.
/// </summary>
public sealed class CompaniesClearAtsCommand : ICliCommand
{
    public string Name => "companies-clear-ats";
    public string Description => "Clear a company's ats_type/ats_slug. Usage: companies-clear-ats <domain> [reason]";

    public int Run(string[] args, IServiceProvider services)
    {
        if (args.Length < 1) { Console.WriteLine(Description); return 2; }

        var domain = args[0];
        var reason = args.Length > 1 ? string.Join(" ", args[1..]) : "manually cleared";

        var repo = services.GetRequiredService<ICompanyRepository>();
        var c = repo.GetByDomain(domain);
        if (c is null) { Console.WriteLine($"No company with domain '{domain}'"); return 1; }

        repo.ClearAtsSlug(c.Id, reason);
        Console.WriteLine($"Cleared ATS info for {c.Name} ({c.Domain}) — was {c.AtsType}:{c.AtsSlug}");
        return 0;
    }
}
