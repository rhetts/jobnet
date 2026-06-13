using System;
using System.Threading;
using Jobnet.Data.Repositories;
using Jobnet.Services.JobSources;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class RefreshJobsCommand : ICliCommand
{
    public string Name => "refresh-jobs";
    public string Description =>
        "Refresh jobs. Usage: refresh-jobs [--company <domain>] [--native-only] [--skip-recent <days>]";

    public int Run(string[] args, IServiceProvider services)
    {
        var refresher = services.GetRequiredService<IJobRefresher>();
        var companies = services.GetRequiredService<ICompanyRepository>();

        string? domain = null;
        var nativeOnly = false;
        var skipRecent = 0;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--company"      when i + 1 < args.Length: domain = args[++i]; break;
                case "--native-only":                            nativeOnly = true; break;
                case "--skip-recent"  when i + 1 < args.Length:
                    int.TryParse(args[++i], out skipRecent); break;
            }
        }

        // Single-company mode: simple path, no live progress needed.
        if (domain is not null)
        {
            var company = companies.GetByDomain(domain);
            if (company is null) { Console.WriteLine($"No company with domain '{domain}'"); return 1; }
            Console.WriteLine($"Refreshing {company.Name} ({company.Domain}) via {company.AtsType ?? "(no ATS)"}...");
            var single = refresher.RefreshAsync(company).GetAwaiter().GetResult();
            PrintReport(single);
            return single.CompaniesFailed > 0 ? 1 : 0;
        }

        // Batch mode. Live per-company progress so the user can watch it run.
        Console.WriteLine($"Refreshing all companies (native-only={nativeOnly}, skip-recent={skipRecent}d)...");
        Console.WriteLine();

        var progress = new Progress<JobRefreshProgress>(p =>
        {
            if (p.Stage == "starting")
            {
                Console.Write($"[{p.Current,4}/{p.Total}] {p.CompanyName,-30} {p.CompanyDomain,-32} ");
            }
            else // done
            {
                // Compute deltas from the cumulative totals so the line shows per-company stats.
                // We don't have the prior cumulative here (single-pass IProgress) so just show
                // the running totals — enough to track progress.
                Console.WriteLine($"done   total: {p.JobsAddedSoFar} added, {p.JobsUpdatedSoFar} updated, {p.ErrorsSoFar} errors");
            }
        });

        JobRefreshReport report;
        if (nativeOnly)
        {
            // Iterate native companies ourselves so we get per-step progress AND skip the
            // AI-extract tail. RefreshAllAsync would include everything; we want to demo just
            // the JSON-API path.
            report = RefreshNativeOnly(refresher, companies, skipRecent, CancellationToken.None);
        }
        else
        {
            report = refresher.RefreshAllAsync(minDaysSinceLastScan: skipRecent, progress: progress,
                                                ct: CancellationToken.None).GetAwaiter().GetResult();
        }

        Console.WriteLine();
        PrintReport(report);
        return report.CompaniesFailed > 0 ? 1 : 0;
    }

    /// <summary>Refresh only companies that have <c>ats_type</c> + <c>ats_slug</c> set, with
    /// per-company progress to the console. Used by <c>--native-only</c> to demonstrate the
    /// JSON-API adapters without grinding through the slow AI-extract tail.</summary>
    private static JobRefreshReport RefreshNativeOnly(IJobRefresher refresher,
        ICompanyRepository companies, int skipRecent, CancellationToken ct)
    {
        var all = companies.GetAll();
        var cutoff = skipRecent > 0 ? (DateTime?)DateTime.UtcNow.AddDays(-skipRecent) : null;
        var eligible = new System.Collections.Generic.List<Jobnet.Models.Company>();
        foreach (var c in all)
        {
            if (string.IsNullOrEmpty(c.AtsType) || string.IsNullOrEmpty(c.AtsSlug)) continue;
            if (cutoff.HasValue && c.DateLastScan.HasValue && c.DateLastScan.Value > cutoff.Value) continue;
            eligible.Add(c);
        }

        var totalProcessed = 0; var totalFailed = 0;
        var totalAdded = 0; var totalUpdated = 0; var totalRemoved = 0;
        var errors = new System.Collections.Generic.List<string>();
        for (var i = 0; i < eligible.Count; i++)
        {
            var c = eligible[i];
            ct.ThrowIfCancellationRequested();
            Console.Write($"[{i + 1,4}/{eligible.Count}] {c.Name,-30} {c.Domain,-32} via {c.AtsType,-16} ");
            try
            {
                var r = refresher.RefreshAsync(c, ct).GetAwaiter().GetResult();
                totalProcessed += r.CompaniesProcessed;
                totalFailed    += r.CompaniesFailed;
                totalAdded     += r.JobsAdded;
                totalUpdated   += r.JobsUpdated;
                totalRemoved   += r.JobsRemoved;
                errors.AddRange(r.Errors);
                Console.WriteLine($"+{r.JobsAdded} ~{r.JobsUpdated} -{r.JobsRemoved}");
            }
            catch (Exception ex)
            {
                totalFailed++;
                errors.Add($"[{c.Domain}] {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"FAILED ({ex.GetType().Name})");
            }
        }
        return new JobRefreshReport
        {
            CompaniesProcessed = totalProcessed, CompaniesFailed = totalFailed,
            JobsAdded = totalAdded, JobsUpdated = totalUpdated, JobsRemoved = totalRemoved,
            Errors = errors,
        };
    }

    private static void PrintReport(JobRefreshReport report)
    {
        Console.WriteLine($"Companies processed:  {report.CompaniesProcessed}");
        Console.WriteLine($"Companies skipped:    {report.CompaniesSkippedNoAts} (no ATS detected)");
        Console.WriteLine($"Companies failed:     {report.CompaniesFailed}");
        Console.WriteLine($"Jobs added:           {report.JobsAdded}");
        Console.WriteLine($"Jobs updated:         {report.JobsUpdated}");
        Console.WriteLine($"Jobs marked removed:  {report.JobsRemoved}");
        if (report.Errors.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Errors ({report.Errors.Count}):");
            foreach (var e in report.Errors) Console.WriteLine($"  ! {e}");
        }
    }
}
