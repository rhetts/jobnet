using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Jobnet.Models;
using Jobnet.Data.Repositories;
using Jobnet.Services.AtsDetection;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class DetectAtsCommand : ICliCommand
{
    public string Name => "detect-ats";
    public string Description => "Detect ATS for a company. Usage: detect-ats <domain> | --all | --missing | "
                               + "--eval [--limit N] [--legacy]";

    // ATS types detection has URL patterns for — the only ones --eval can fairly score.
    private static readonly HashSet<string> DetectableAts = new(StringComparer.OrdinalIgnoreCase)
    {
        "greenhouse", "lever", "ashby", "workable", "smartrecruiters", "recruitee",
        "bamboohr", "pinpoint", "workday", "risepeople", "cornerstone",
    };

    public int Run(string[] args, IServiceProvider services)
    {
        if (args.Length < 1)
        {
            Console.WriteLine(Description);
            return 2;
        }

        var companies = services.GetRequiredService<ICompanyRepository>();
        var detector  = services.GetRequiredService<IAtsDetector>();

        if (args[0] == "--eval") return RunEval(args, companies, detector);

        if (args[0] == "--all" || args[0] == "--missing")
        {
            var onlyMissing = args[0] == "--missing";
            var all = companies.GetAll();
            var processed = 0;
            var hits = 0;
            foreach (var c in all)
            {
                if (onlyMissing && !string.IsNullOrEmpty(c.AtsType)) continue;
                Console.WriteLine($"[{c.Name}] {c.Domain}");
                var result = detector.DetectAsync(c).GetAwaiter().GetResult();
                processed++;
                ApplyHealth(companies, c, result);
                if (result.AtsType is not null)
                {
                    hits++;
                    companies.SetAtsInfo(c.Id, result.AtsType, result.AtsSlug, result.ResolvedCareersUrl);
                    Console.WriteLine($"  → {result.AtsType}:{result.AtsSlug}  (via {result.Source})  {result.ResolvedCareersUrl}");
                }
                else
                {
                    Console.WriteLine($"  → no ATS detected. {result.Notes}");
                }
            }
            Console.WriteLine();
            Console.WriteLine($"Processed {processed} compan{(processed == 1 ? "y" : "ies")}, {hits} ATS hits.");
            return 0;
        }

        var domain = args[0];
        var company = companies.GetByDomain(domain);
        if (company is null) { Console.WriteLine($"No company with domain '{domain}'"); return 1; }

        var r = detector.DetectAsync(company).GetAwaiter().GetResult();
        ApplyHealth(companies, company, r);
        Console.WriteLine($"Domain:          {company.Domain}");
        Console.WriteLine($"Source:          {r.Source}");
        Console.WriteLine($"ATS type:        {r.AtsType ?? "(none)"}");
        Console.WriteLine($"ATS slug:        {r.AtsSlug ?? "(none)"}");
        Console.WriteLine($"Resolved URL:    {r.ResolvedCareersUrl ?? "(none)"}");
        if (!string.IsNullOrEmpty(r.Notes)) Console.WriteLine($"Notes:           {r.Notes}");

        if (r.AtsType is not null)
        {
            companies.SetAtsInfo(company.Id, r.AtsType, r.AtsSlug, r.ResolvedCareersUrl);
            Console.WriteLine();
            Console.WriteLine("Persisted to DB.");
        }
        return 0;
    }

    /// <summary>Score detection against companies whose ATS is already known: detect each one as if
    /// it were new (ATS and careers URL hidden from the detector) and compare. Read-only — nothing
    /// is persisted. Same shuffled sample every time (fixed seed), so --legacy runs are comparable.</summary>
    private static int RunEval(string[] args, ICompanyRepository companies, IAtsDetector detector)
    {
        var limit = 0;
        var li = Array.IndexOf(args, "--limit");
        if (li >= 0 && li + 1 < args.Length) int.TryParse(args[li + 1], out limit);
        var method = args.Contains("--legacy") ? AtsDetectionMethod.Legacy : AtsDetectionMethod.HomepageFirst;

        var rng = new Random(42);
        var sample = companies.GetAll()
            .Where(c => c.IsActive && !c.IsBlacklisted && !string.IsNullOrEmpty(c.AtsSlug)
                        && c.AtsType is not null && DetectableAts.Contains(c.AtsType))
            .OrderBy(c => c.Id).OrderBy(_ => rng.Next())
            .ToList();
        if (limit > 0) sample = sample.Take(limit).ToList();

        Console.WriteLine($"Eval: {method}, {sample.Count} companies with a known ATS. Nothing is saved.");
        Console.WriteLine();

        var rows = new List<(string Verdict, double Secs)>();
        var i = 0;
        foreach (var c in sample)
        {
            i++;
            var blind = new Company { Id = c.Id, Name = c.Name, Domain = c.Domain, WebsiteUrl = c.WebsiteUrl, City = c.City };
            var sw = Stopwatch.StartNew();
            AtsDetectionResult r;
            try { r = detector.DetectWithAsync(blind, method).GetAwaiter().GetResult(); }
            catch (Exception ex) { r = new AtsDetectionResult { Source = "error", Notes = $"{ex.GetType().Name}: {ex.Message}" }; }
            sw.Stop();

            var expected = $"{c.AtsType}:{c.AtsSlug}";
            var got = r.AtsType is null ? "-" : $"{r.AtsType}:{r.AtsSlug ?? "?"}";
            var verdict =
                r.AtsType is null ? (r.Source switch { "site_unreachable" => "DEAD", "site_moved" => "MOVED", _ => "MISS" })
                : string.Equals(r.AtsType, c.AtsType, StringComparison.OrdinalIgnoreCase)
                  && string.Equals(r.AtsSlug, c.AtsSlug, StringComparison.OrdinalIgnoreCase) ? "OK"
                : string.Equals(r.AtsType, c.AtsType, StringComparison.OrdinalIgnoreCase) && r.AtsSlug is null ? "HINT"
                : "WRONG";
            rows.Add((verdict, sw.Elapsed.TotalSeconds));

            Console.WriteLine($"[{i}/{sample.Count}] {verdict,-5} {sw.Elapsed.TotalSeconds,5:F1}s  {c.Name} ({c.Domain})  expected {expected}  got {got}  via {r.Source}");
            if (verdict != "OK" && !string.IsNullOrEmpty(r.Notes))
                Console.WriteLine($"         {Truncate(r.Notes!, 600)}");
        }

        if (rows.Count == 0) return 0;
        var secs = rows.Select(x => x.Secs).OrderBy(x => x).ToList();
        Console.WriteLine();
        Console.WriteLine($"Results ({method}, {rows.Count} companies):");
        foreach (var v in new[] { "OK", "WRONG", "HINT", "MISS", "DEAD", "MOVED" })
        {
            var n = rows.Count(x => x.Verdict == v);
            Console.WriteLine($"  {v,-6} {n,4}  ({100.0 * n / rows.Count:F0}%)");
        }
        Console.WriteLine($"  Time: total {secs.Sum() / 60:F1} min, mean {secs.Average():F1}s, median {secs[secs.Count / 2]:F1}s, " +
                          $"p90 {secs[(int)(secs.Count * 0.9)]:F1}s, max {secs[^1]:F1}s");
        return 0;
    }

    /// <summary>Record what detection learned about the site (dead, moved, no careers page) — or
    /// clear an old flag when it found an ATS. Plain misses leave the flag alone.</summary>
    private static void ApplyHealth(ICompanyRepository companies, Company c, AtsDetectionResult r)
    {
        var (apply, status, reason) = r.HealthUpdate();
        if (!apply || (status is null && c.HealthStatus is null)) return;
        companies.SetHealth(c.Id, status, reason);
        if (status is not null) Console.WriteLine($"  ⚠ {CompanyHealth.Label(status)}: {reason}");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
