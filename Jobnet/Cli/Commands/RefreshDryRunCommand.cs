using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

/// <summary>
/// Read-only health check for every native-ATS-configured company. Pings each slug with a
/// cheap <c>GET ?limit=1</c> (or the equivalent for that ATS) and reports the HTTP status —
/// no DB writes, no job ingestion. Catches stale-slug rot proactively before a real refresh
/// has to discover the same 4xxs the slow way.
///
/// Usage: <c>refresh-jobs-dry-run</c> (no args)
///        <c>refresh-jobs-dry-run --ats workday</c>     filter to one ATS type
///        <c>refresh-jobs-dry-run --bad-only</c>        only print non-2xx rows
/// </summary>
public sealed class RefreshDryRunCommand : ICliCommand
{
    public string Name => "refresh-jobs-dry-run";
    public string Description =>
        "Probe every native-ATS slug without refreshing. Flags: --ats <type>, --bad-only.";

    public int Run(string[] args, IServiceProvider services)
    {
        var atsFilter = ParseArg(args, "--ats");
        var badOnly = args.Contains("--bad-only");

        var repo = services.GetRequiredService<ICompanyRepository>();
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Jobnet/dry-run");

        var companies = repo.GetAll()
            .Where(c => c.IsActive && !string.IsNullOrEmpty(c.AtsType) && !string.IsNullOrEmpty(c.AtsSlug))
            .Where(c => atsFilter is null || string.Equals(c.AtsType, atsFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.AtsType).ThenBy(c => c.Name)
            .ToList();

        if (companies.Count == 0)
        {
            Console.WriteLine("(no native-ATS companies match the filter)");
            return 0;
        }

        Console.WriteLine($"Dry-run probing {companies.Count} native-ATS companies...");
        Console.WriteLine();
        Console.WriteLine($"{"ATS",-15}  {"Slug",-44}  {"Company",-30}  Status");
        Console.WriteLine(new string('-', 110));

        var bad = 0;
        var probed = 0;
        foreach (var c in companies)
        {
            probed++;
            var (status, summary) = ProbeAsync(http, c).GetAwaiter().GetResult();
            var isBad = status is null || status >= 400 || status == 0;
            if (badOnly && !isBad) continue;
            if (isBad) bad++;
            var statusText = status is null ? "ERR" : status.ToString()!;
            Console.WriteLine($"{c.AtsType,-15}  {Trunc(c.AtsSlug!, 44),-44}  {Trunc(c.Name, 30),-30}  {statusText}  {summary}");
        }

        Console.WriteLine();
        Console.WriteLine($"Probed {probed} companies, {bad} returned 4xx/5xx/error.");
        return bad > 0 ? 1 : 0;
    }

    private static async Task<(int? Status, string Summary)> ProbeAsync(HttpClient http, Company c)
    {
        // One small GET per ATS — same endpoints the real adapter uses, but with limit=1
        // where the API supports it. Goal: cheap 200 vs 404 verification, NOT job parsing.
        var url = c.AtsType switch
        {
            "greenhouse"      => $"https://boards-api.greenhouse.io/v1/boards/{c.AtsSlug}/jobs",
            "lever"           => $"https://api.lever.co/v0/postings/{c.AtsSlug}?mode=json&limit=1",
            "ashby"           => $"https://api.ashbyhq.com/posting-api/job-board/{c.AtsSlug}",
            "workable"        => $"https://apply.workable.com/api/v1/widget/accounts/{c.AtsSlug}",
            "smartrecruiters" => $"https://api.smartrecruiters.com/v1/companies/{c.AtsSlug}/postings?limit=1",
            "pinpoint"        => $"https://{c.AtsSlug}.pinpointhq.com/postings.json",
            "bamboohr"        => $"https://{c.AtsSlug}.bamboohr.com/careers/list",
            "amazon"          => "https://www.amazon.jobs/en/search.json?normalized_state_name%5B%5D=British+Columbia&result_limit=1",
            _                 => null,
        };
        if (url is null) return (null, "(unknown ATS type)");

        try
        {
            using var resp = await http.GetAsync(url);
            return ((int)resp.StatusCode, "");
        }
        catch (Exception ex)
        {
            return (null, ex.Message.Split('\n')[0]);
        }
    }

    private static string? ParseArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private static string Trunc(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" :
        s.Length <= n ? s : s.Substring(0, n - 1) + "…";
}
