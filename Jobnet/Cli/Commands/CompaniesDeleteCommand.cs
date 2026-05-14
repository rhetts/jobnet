using System;
using Dapper;
using Jobnet.Data;
using Jobnet.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class CompaniesDeleteCommand : ICliCommand
{
    public string Name => "companies-delete";
    public string Description =>
        "Delete companies. Usage: companies-delete <domain>  |  --id <n>  |  --discovered-today  |  --all";

    public int Run(string[] args, IServiceProvider services)
    {
        if (args.Length < 1)
        {
            Console.WriteLine(Description);
            return 2;
        }

        var connections = services.GetRequiredService<IDbConnectionFactory>();
        var repo = services.GetRequiredService<ICompanyRepository>();

        // --all: nuke everything
        if (args[0] == "--all")
        {
            using var conn = connections.Open();
            var count = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM companies");
            if (count == 0) { Console.WriteLine("(no companies to delete)"); return 0; }
            using var tx = conn.BeginTransaction();
            conn.Execute("DELETE FROM job_areas WHERE job_id IN (SELECT id FROM jobs)", transaction: tx);
            conn.Execute("DELETE FROM jobs", transaction: tx);
            conn.Execute("DELETE FROM companies", transaction: tx);
            tx.Commit();
            Console.WriteLine($"Deleted {count} compan{(count == 1 ? "y" : "ies")} (and all their jobs).");
            return 0;
        }

        // --discovered-today: delete companies discovered today UTC
        if (args[0] == "--discovered-today")
        {
            using var conn = connections.Open();
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var ids = conn.Query<int>(
                "SELECT id FROM companies WHERE substr(date_discovered, 1, 10) = @today",
                new { today }).ToList();
            if (ids.Count == 0) { Console.WriteLine("(no companies discovered today)"); return 0; }
            DeleteByIds(conn, ids);
            Console.WriteLine($"Deleted {ids.Count} compan{(ids.Count == 1 ? "y" : "ies")} discovered today.");
            return 0;
        }

        // --id <n>: delete a single company by ID
        if (args[0] == "--id" && args.Length >= 2 && int.TryParse(args[1], out var id))
        {
            var company = repo.GetById(id);
            if (company is null) { Console.WriteLine($"No company with id {id}"); return 1; }
            using var conn = connections.Open();
            DeleteByIds(conn, new[] { id });
            Console.WriteLine($"Deleted company id {id}: {company.Name} ({company.Domain})");
            return 0;
        }

        // default: treat arg as a domain
        var domain = args[0];
        var c = repo.GetByDomain(domain);
        if (c is null) { Console.WriteLine($"No company with domain '{domain}'"); return 1; }
        using (var conn = connections.Open())
            DeleteByIds(conn, new[] { c.Id });
        Console.WriteLine($"Deleted company {c.Name} ({c.Domain})");
        return 0;
    }

    private static void DeleteByIds(System.Data.IDbConnection conn, System.Collections.Generic.IReadOnlyCollection<int> ids)
    {
        using var tx = conn.BeginTransaction();
        conn.Execute(
            "DELETE FROM job_areas WHERE job_id IN (SELECT id FROM jobs WHERE company_id IN @ids)",
            new { ids }, tx);
        conn.Execute("DELETE FROM jobs WHERE company_id IN @ids", new { ids }, tx);
        conn.Execute("DELETE FROM companies WHERE id IN @ids", new { ids }, tx);
        tx.Commit();
    }
}
