using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using Jobnet.Data;
using Jobnet.Data.Repositories;
using Jobnet.Services.Technology;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class ExtractTechnologiesCommand : ICliCommand
{
    public string Name => "extract-technologies";
    public string Description =>
        "Re-run the technology matcher against every active job and rewrite job_technologies. " +
        "Idempotent — safe to run any time. Flags: --include-summary (default on), --include-removed";

    public int Run(string[] args, IServiceProvider services)
    {
        var includeSummary = !args.Contains("--no-summary");
        var includeRemoved = args.Contains("--include-removed");

        var conns   = services.GetRequiredService<IDbConnectionFactory>();
        var techs   = services.GetRequiredService<ITechnologyRepository>();
        var matcher = services.GetRequiredService<ITechnologyMatcher>();

        using var conn = conns.Open();
        var whereActive = includeRemoved ? "" : "WHERE is_active = 1";
        var rows = conn.Query<(int Id, string? Title, string? Snippet, string? Summary)>(
            $"SELECT id, title, description_snippet, summary FROM jobs {whereActive}").ToList();

        Console.WriteLine($"Scanning {rows.Count} jobs " +
                          $"({(includeSummary ? "title + snippet + summary" : "title + snippet")})...");

        var totalTags = 0;
        var jobsWithTags = 0;
        var stepReport = Math.Max(50, rows.Count / 20);
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var text = (r.Title ?? "") + "\n" + (r.Snippet ?? "");
            if (includeSummary) text += "\n" + (r.Summary ?? "");
            var ids = matcher.Match(text);
            techs.SetForJob(r.Id, ids);
            if (ids.Count > 0)
            {
                totalTags += ids.Count;
                jobsWithTags++;
            }
            if ((i + 1) % stepReport == 0)
                Console.WriteLine($"  {i + 1}/{rows.Count} scanned, {jobsWithTags} tagged so far");
        }

        Console.WriteLine();
        Console.WriteLine($"Done. {jobsWithTags} jobs tagged with {totalTags} tags " +
                          $"({(rows.Count == 0 ? 0 : (double)totalTags / rows.Count):F1} avg per job).");

        // Top-10 by activity for a sanity check.
        var counts = techs.GetActiveCountsByTechnology();
        var all = techs.GetAll().ToDictionary(t => t.Id);
        Console.WriteLine();
        Console.WriteLine("Top 10 technologies (across active jobs):");
        foreach (var (techId, n) in counts.OrderByDescending(kv => kv.Value).Take(10))
        {
            var name = all.TryGetValue(techId, out var t) ? t.Name : $"#{techId}";
            Console.WriteLine($"  {name,-22} {n,5} jobs");
        }
        return 0;
    }
}
