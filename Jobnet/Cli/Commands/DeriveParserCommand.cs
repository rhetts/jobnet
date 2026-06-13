using System;
using System.Linq;
using System.Threading;
using Jobnet.Data.Repositories;
using Jobnet.Services.JobSources;
using Jobnet.Services.Parsing;
using Jobnet.Services.Playwright;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

/// <summary>
/// Diagnostic CLI that exercises the selector-derivation pipeline end-to-end on one company,
/// printing each stage so we can see exactly where it breaks. Mirrors what the Re-derive
/// button does, but with full visibility into the Playwright fetch, the AI response, the
/// extracted JSON, the parsed profile, and the SelectorProfileReplayer sanity check.
///
/// Usage:
///   Jobnet.exe derive-parser --company shopify.com
///   Jobnet.exe derive-parser --company shopify.com --url https://shopify.com/careers
///   Jobnet.exe derive-parser --company shopify.com --no-persist   (skip the DB write)
/// </summary>
public sealed class DeriveParserCommand : ICliCommand
{
    public string Name => "derive-parser";
    public string Description =>
        "Derive a selector profile for one company and print each stage. " +
        "Usage: derive-parser --company <domain> [--url <override>] [--no-persist]";

    public int Run(string[] args, IServiceProvider services)
    {
        string? domain = null;
        string? urlOverride = null;
        string? dumpHtmlPath = null;
        var persist = true;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--company"   when i + 1 < args.Length: domain = args[++i]; break;
                case "--url"       when i + 1 < args.Length: urlOverride = args[++i]; break;
                case "--dump-html" when i + 1 < args.Length: dumpHtmlPath = args[++i]; break;
                case "--no-persist": persist = false; break;
            }
        }
        if (string.IsNullOrEmpty(domain))
        {
            Console.WriteLine(Description);
            return 2;
        }

        var companies = services.GetRequiredService<ICompanyRepository>();
        var fetcher   = services.GetRequiredService<IPlaywrightFetcher>();
        var parser    = services.GetRequiredService<SelectorProfileReplayer>();
        var deriver   = services.GetRequiredService<AiSelectorDeriver>();

        var company = companies.GetByDomain(domain);
        if (company is null) { Console.WriteLine($"No company with domain '{domain}'"); return 1; }

        var url = urlOverride
                  ?? company.CareersUrl
                  ?? company.WebsiteUrl
                  ?? $"https://{company.Domain}/careers";

        Header("Target");
        Console.WriteLine($"  company:        #{company.Id} {company.Name} ({company.Domain})");
        Console.WriteLine($"  url:            {url}");
        Console.WriteLine($"  ats:            {company.AtsType ?? "(none)"}");
        Console.WriteLine($"  current profile: {(string.IsNullOrEmpty(company.ParserStrategy) ? "(none cached)" : $"{company.ParserStrategy!.Length} chars")}");
        Console.WriteLine($"  disabled:       {company.ParserStrategyDisabled}");
        Console.WriteLine($"  last result:    {company.ParserStrategyLastResult ?? "—"} ({company.ParserStrategyLastResultAt?.ToLocalTime():yyyy-MM-dd HH:mm:ss})");
        Console.WriteLine($"  persist:        {persist}");

        Header("Stage 1 — Playwright fetch");
        PlaywrightFetchResult fetch;
        try
        {
            fetch = fetcher.FetchAsync(url, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        Console.WriteLine($"  http status:    {fetch.HttpStatus}");
        Console.WriteLine($"  final url:      {fetch.FinalUrl}");
        Console.WriteLine($"  success:        {fetch.Success}");
        Console.WriteLine($"  html size:      {fetch.Html?.Length ?? 0} chars");
        Console.WriteLine($"  network reqs:   {fetch.NetworkRequests.Count}");
        if (!fetch.Success || string.IsNullOrEmpty(fetch.Html))
        {
            Console.WriteLine($"  error:          {fetch.Error ?? "(unknown)"}");
            return 1;
        }

        if (!string.IsNullOrEmpty(dumpHtmlPath))
        {
            System.IO.File.WriteAllText(dumpHtmlPath, fetch.Html);
            Console.WriteLine($"  dumped html →   {dumpHtmlPath}");
        }

        Header("Stage 2 — AI derivation");
        SelectorDeriveResult result;
        try
        {
            result = deriver.DeriveAsync(fetch.Html, fetch.FinalUrl, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        if (!result.Success)
        {
            Console.WriteLine($"  result:         FAIL");
            Console.WriteLine($"  reason:         {result.Error}");
            if (!string.IsNullOrEmpty(result.ProposedProfileJson))
            {
                Console.WriteLine();
                Console.WriteLine("  --- proposed profile (AI emitted, replay rejected) ---");
                Console.WriteLine(result.ProposedProfileJson);
            }
            Console.WriteLine();
            Console.WriteLine($"  See {System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jobnet", "ai-parse-errors.log")} for the raw AI response.");
            return 1;
        }

        Console.WriteLine($"  result:         OK");
        Console.WriteLine($"  sanity jobs:    {result.Jobs.Count}");
        Console.WriteLine($"  profile bytes:  {result.ProfileJson?.Length ?? 0}");
        Console.WriteLine();
        Console.WriteLine("  --- profile ---");
        Console.WriteLine(result.ProfileJson);
        Console.WriteLine("  --- sample (first 3 jobs from selector replay) ---");
        foreach (var j in result.Jobs.Take(3))
            Console.WriteLine($"    • {j.Title}  →  {j.Url}");

        if (persist)
        {
            Header("Stage 3 — Persist");
            companies.SetParserStrategy(company.Id, result.ProfileJson!, DateTime.UtcNow);
            companies.SetParserStrategyResult(company.Id, "ok", DateTime.UtcNow, errorMessage: null);
            Console.WriteLine($"  saved profile for company #{company.Id}.");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("  (--no-persist set; DB not updated)");
        }
        return 0;
    }

    private static void Header(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {title} ===");
    }
}
