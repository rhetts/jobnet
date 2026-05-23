using System;
using System.Linq;
using Jobnet.Services.Resume;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class ResumeCommand : ICliCommand
{
    public string Name => "resume";
    public string Description => "Upload a resume PDF and/or match it against all active jobs.\n" +
                                  "  resume upload <path.pdf>    Parse + store resume text\n" +
                                  "  resume match [--max N]      Score every active job against the stored resume\n" +
                                  "  resume status               Show whether a resume is loaded\n" +
                                  "  resume set-scores <file>    Apply a JSON file of {id,score,reason} entries";

    public int Run(string[] args, IServiceProvider services)
    {
        if (args.Length == 0) { Console.WriteLine(Description); return 1; }
        var matcher = services.GetRequiredService<IResumeMatcher>();
        var sub = args[0].ToLowerInvariant();

        switch (sub)
        {
            case "upload":
            {
                if (args.Length < 2) { Console.WriteLine("Usage: resume upload <path.pdf>"); return 1; }
                var r = matcher.UploadResumeAsync(args[1]).GetAwaiter().GetResult();
                if (!r.Success) { Console.WriteLine($"Upload failed: {r.Error}"); return 1; }
                Console.WriteLine($"Resume uploaded: {r.Pages} page(s), {r.Characters} chars.");
                return 0;
            }
            case "match":
            {
                int max = 500;
                for (var i = 1; i < args.Length - 1; i++)
                    if (args[i] == "--max" && int.TryParse(args[i + 1], out var n)) max = n;
                Console.WriteLine($"Matching up to {max} jobs against the stored resume...");
                var r = matcher.MatchAllAsync(max).GetAwaiter().GetResult();
                Console.WriteLine();
                Console.WriteLine($"Examined: {r.JobsExamined}");
                Console.WriteLine($"Scored:   {r.JobsScored}");
                Console.WriteLine($"Failed:   {r.JobsFailed}");
                if (r.Errors.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("Errors:");
                    foreach (var e in r.Errors) Console.WriteLine($"  ! {e}");
                }
                if (r.JobsScored == 0 && matcher is Jobnet.Services.Resume.ResumeMatcher concrete && !string.IsNullOrEmpty(concrete.LastRawResponse))
                {
                    Console.WriteLine();
                    Console.WriteLine($"Last raw AI response ({concrete.LastRawResponse!.Length} chars):");
                    Console.WriteLine(concrete.LastRawResponse.Length <= 1500 ? concrete.LastRawResponse : concrete.LastRawResponse.Substring(0, 1500) + "...");
                }
                return r.JobsFailed > 0 ? 1 : 0;
            }
            case "status":
            {
                var text = matcher.GetStoredResume();
                var path = matcher.GetStoredResumeSourcePath();
                if (string.IsNullOrEmpty(text)) { Console.WriteLine("(no resume loaded)"); return 0; }
                Console.WriteLine($"Resume loaded ({text!.Length} chars)");
                if (!string.IsNullOrEmpty(path)) Console.WriteLine($"Source: {path}");
                Console.WriteLine();
                Console.WriteLine("Preview:");
                Console.WriteLine(text.Length <= 400 ? text : text.Substring(0, 400) + "...");
                return 0;
            }
            case "dump-unscored":
            {
                var jobs = services.GetRequiredService<Jobnet.Data.Repositories.IJobRepository>();
                var companies = services.GetRequiredService<Jobnet.Data.Repositories.ICompanyRepository>().GetAll()
                    .ToDictionary(c => c.Id, c => c.Name);
                var unscored = jobs.GetAll(includeRemoved: false)
                    .Where(j => !j.ResumeMatchScore.HasValue)
                    .Select(j => new
                    {
                        id = j.Id,
                        title = j.Title,
                        company = companies.TryGetValue(j.CompanyId, out var n) ? n : $"#{j.CompanyId}",
                        location = j.Location,
                        summary = j.Summary,
                        desc = j.DescriptionSnippet,
                    })
                    .ToList();
                Console.Write(System.Text.Json.JsonSerializer.Serialize(unscored,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = false }));
                return 0;
            }
            case "set-scores":
            {
                if (args.Length < 2) { Console.WriteLine("Usage: resume set-scores <path-to-json>"); return 1; }
                var path = args[1];
                if (!System.IO.File.Exists(path)) { Console.WriteLine($"File not found: {path}"); return 1; }
                var jobs = services.GetRequiredService<Jobnet.Data.Repositories.IJobRepository>();
                using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
                int applied = 0, skipped = 0;
                foreach (var elt in doc.RootElement.EnumerateArray())
                {
                    if (!elt.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id)) { skipped++; continue; }
                    if (!elt.TryGetProperty("score", out var scEl) || !scEl.TryGetInt32(out var sc)) { skipped++; continue; }
                    var reason = elt.TryGetProperty("reason", out var rEl) && rEl.ValueKind == System.Text.Json.JsonValueKind.String ? rEl.GetString() ?? "" : "";
                    jobs.SetResumeMatch(id, Math.Clamp(sc, 0, 100), reason);
                    applied++;
                }
                Console.WriteLine($"Applied {applied} score(s); skipped {skipped}.");
                return 0;
            }
            default:
                Console.WriteLine($"Unknown sub-command '{sub}'. Try: upload, match, status, set-scores.");
                return 1;
        }
    }
}
