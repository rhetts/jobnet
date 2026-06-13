using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using Jobnet.Data;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

/// <summary>
/// Snapshot of which extraction system serves which companies. Same precedence as the
/// Parser Report screen, but pivoted into a CLI-friendly table: per-system count of
/// companies and total active jobs they contribute. Useful for tracking adapter ROI
/// over time without opening the WPF window.
/// </summary>
public sealed class ParserStatsCommand : ICliCommand
{
    public string Name => "parser-stats";
    public string Description => "Stats on which parser/ATS serves each company. Flags: --include-inactive, --candidates";

    public int Run(string[] args, IServiceProvider services)
    {
        var includeInactive = args.Contains("--include-inactive");
        var candidates = args.Contains("--candidates");

        var repo = services.GetRequiredService<ICompanyRepository>();
        var jobs = services.GetRequiredService<IJobRepository>();
        var counts = jobs.GetActiveCountsByCompany();

        if (candidates)
        {
            return ShowCandidates(services, jobs, repo, counts);
        }

        var all = repo.GetAll();
        var rows = (includeInactive ? all : all.Where(c => c.IsActive)).ToList();

        var totalActive = all.Count(c => c.IsActive);
        var totalInactive = all.Count - totalActive;
        Console.WriteLine($"Companies — active: {totalActive}, inactive: {totalInactive}, total: {all.Count}");
        Console.WriteLine();

        // Categorize each company once, then aggregate twice (detailed and rolled-up).
        var entries = rows.Select(c => new
        {
            Company = c,
            Jobs = counts.TryGetValue(c.Id, out var n) ? n : 0,
            Detail = Categorize(c),
        }).ToList();

        Console.WriteLine("=== By parser system (detailed) ===");
        Console.WriteLine($"{"system",-36}  {"companies",10}  {"jobs",6}");
        Console.WriteLine(new string('-', 60));
        foreach (var g in entries.GroupBy(e => e.Detail)
                                 .OrderByDescending(g => g.Sum(x => x.Jobs))
                                 .ThenByDescending(g => g.Count()))
        {
            Console.WriteLine($"{g.Key,-36}  {g.Count(),10}  {g.Sum(x => x.Jobs),6}");
        }
        Console.WriteLine();

        Console.WriteLine("=== Rolled up by top-level system ===");
        Console.WriteLine($"{"system",-36}  {"companies",10}  {"jobs",6}");
        Console.WriteLine(new string('-', 60));
        foreach (var g in entries.GroupBy(e => Rollup(e.Detail))
                                 .OrderByDescending(g => g.Sum(x => x.Jobs))
                                 .ThenByDescending(g => g.Count()))
        {
            Console.WriteLine($"{g.Key,-36}  {g.Count(),10}  {g.Sum(x => x.Jobs),6}");
        }
        return 0;
    }

    private static string Categorize(Company c)
    {
        if (!string.IsNullOrEmpty(c.AtsType) && !string.IsNullOrEmpty(c.AtsSlug))
            return $"native ATS ({c.AtsType})";
        if (!string.IsNullOrWhiteSpace(c.LastCompanyParser))
            return $"hand-written ({c.LastCompanyParser})";
        if (!string.IsNullOrWhiteSpace(c.ParserStrategy) && !c.ParserStrategyDisabled)
            return "selectors (AI-derived)";
        if (c.DateLastScan is null)
            return "unknown (never scanned)";
        return "AI extract";
    }

    private static string Rollup(string detail)
    {
        if (detail.StartsWith("native ATS"))   return "native ATS";
        if (detail.StartsWith("hand-written")) return "hand-written";
        return detail;
    }

    /// <summary>List companies most likely to repay a hand-written parser. The signal: this
    /// company has many active jobs *and* the most recent refresh served them via
    /// <c>ai_extract</c> (or via a cached selector profile that's brittle). Sort descending by
    /// active job count, then by recency. The user can pick the top N and write a parser.</summary>
    private static int ShowCandidates(IServiceProvider services,
                                       IJobRepository jobs,
                                       ICompanyRepository repo,
                                       IReadOnlyDictionary<int, int> counts)
    {
        var connFactory = services.GetRequiredService<IDbConnectionFactory>();
        using var conn = connFactory.Open();
        var sourceCounts = conn.Query<SourceCountRow>(@"
            SELECT company_id AS CompanyId, source_stage AS SourceStage, COUNT(*) AS N
            FROM jobs
            WHERE is_active = 1 AND source_stage IS NOT NULL
            GROUP BY company_id, source_stage").ToList();

        var byCompany = sourceCounts
            .GroupBy(r => r.CompanyId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.SourceStage ?? "", r => r.N));

        var companies = repo.GetAll().Where(c => c.IsActive).ToList();

        // Candidate filter: ≥3 active jobs from this company, where ≥80% came via ai_extract
        // (the stage we'd be most willing to replace with a hand-written parser).
        var rows = new List<(Company C, int Total, int AiCount, double AiPct)>();
        foreach (var c in companies)
        {
            if (!byCompany.TryGetValue(c.Id, out var stages)) continue;
            var total = stages.Values.Sum();
            if (total < 3) continue;
            var ai = (stages.TryGetValue("ai_extract", out var n) ? n : 0);
            if (ai == 0) continue;
            var pct = (double)ai / total;
            if (pct < 0.8) continue;
            rows.Add((c, total, ai, pct));
        }

        rows = rows.OrderByDescending(r => r.AiCount).ToList();

        if (rows.Count == 0)
        {
            Console.WriteLine("(no hand-written-parser candidates — no company has ≥3 jobs all served via ai_extract)");
            Console.WriteLine();
            Console.WriteLine("Note: this report relies on source_stage on active jobs, which is populated on");
            Console.WriteLine("every refresh after migration 046. If you haven't refreshed since the schema");
            Console.WriteLine("change, source_stage will be NULL and no candidates will appear. Run");
            Console.WriteLine("refresh-jobs to backfill, then re-run this report.");
            return 0;
        }

        Console.WriteLine("Candidates for hand-written parsers (≥3 jobs, ≥80% via ai_extract):");
        Console.WriteLine();
        Console.WriteLine($"{"Company",-30}  {"Domain",-30}  {"Total",6}  {"AI",6}  AI%");
        Console.WriteLine(new string('-', 90));
        foreach (var r in rows)
        {
            Console.WriteLine($"{Trunc(r.C.Name, 30),-30}  {Trunc(r.C.Domain, 30),-30}  {r.Total,6}  {r.AiCount,6}  {r.AiPct,5:P0}");
        }
        Console.WriteLine();
        Console.WriteLine($"{rows.Count} candidate(s). Pick the top few and add an IHtmlPatternParser.");
        return 0;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";

    /// <summary>Dapper row shape for the source-stage rollup query. Tuple types confuse the
    /// mapper, so use a plain class with named columns.</summary>
    private sealed class SourceCountRow
    {
        public int CompanyId { get; set; }
        public string? SourceStage { get; set; }
        public int N { get; set; }
    }
}
