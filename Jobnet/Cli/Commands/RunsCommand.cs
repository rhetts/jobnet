using System;
using System.Linq;
using Jobnet.Services.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class RunsCommand : ICliCommand
{
    public string Name => "runs";
    public string Description => "List recent runs. Subcommands:\n" +
                                  "  runs [--limit N]      Recent runs (default 30)\n" +
                                  "  runs show <id>        Per-step breakdown of one run";

    public int Run(string[] args, IServiceProvider services)
    {
        var runs = services.GetRequiredService<IRunLogger>();

        if (args.Length >= 2 && args[0].Equals("show", StringComparison.OrdinalIgnoreCase) && long.TryParse(args[1], out var id))
            return ShowOne(runs, id);

        var limit = 30;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--limit" && int.TryParse(args[i + 1], out var n)) limit = n;

        var recent = runs.GetRecent(limit);
        if (recent.Count == 0) { Console.WriteLine("(no runs logged yet)"); return 0; }

        Console.WriteLine($"{"ID",-5}  {"Started (UTC)",-19}  {"Dur",-6}  {"Type",-22}  {"Scope",-26}  {"Status",-9}  {"Exam",-5}  {"Add",-5}  {"Upd",-5}  {"Skp",-5}  {"Fail",-5}  Errs");
        Console.WriteLine(new string('-', 145));
        foreach (var r in recent)
        {
            var dur = r.DurationMs.HasValue ? FormatMs(r.DurationMs.Value) : "—";
            Console.WriteLine($"{r.Id,-5}  {r.StartedAt:yyyy-MM-dd HH:mm:ss}  {dur,-6}  {Trunc(r.RunType, 22),-22}  {Trunc(r.Scope ?? "", 26),-26}  {r.Status,-9}  {r.Examined,-5}  {r.Added,-5}  {r.Updated,-5}  {r.Skipped,-5}  {r.Failed,-5}  {r.ErrorCount}");
        }
        Console.WriteLine();
        Console.WriteLine($"{recent.Count} run(s). Use `runs show <id>` for per-step detail.");
        return 0;
    }

    private static int ShowOne(IRunLogger runs, long id)
    {
        var runRow = runs.GetRecent(1000).FirstOrDefault(r => r.Id == id);
        if (runRow is null) { Console.WriteLine($"No run with id {id}."); return 1; }

        Console.WriteLine($"Run #{runRow.Id} — {runRow.RunType} ({runRow.Scope ?? "(no scope)"})");
        Console.WriteLine($"  Started:   {runRow.StartedAt:yyyy-MM-dd HH:mm:ss} UTC");
        Console.WriteLine($"  Finished:  {(runRow.FinishedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "(still running)")} UTC");
        Console.WriteLine($"  Duration:  {(runRow.DurationMs.HasValue ? FormatMs(runRow.DurationMs.Value) : "—")}");
        Console.WriteLine($"  Status:    {runRow.Status}");
        Console.WriteLine($"  Counts:    examined {runRow.Examined}  added {runRow.Added}  updated {runRow.Updated}  skipped {runRow.Skipped}  failed {runRow.Failed}");
        if (!string.IsNullOrEmpty(runRow.Notes)) Console.WriteLine($"  Notes:     {runRow.Notes}");

        var steps = runs.GetSteps(id);
        if (steps.Count == 0) { Console.WriteLine(); Console.WriteLine("(no per-step records)"); return 0; }

        Console.WriteLine();
        Console.WriteLine($"  {"Step",-40}  {"Dur",-6}  {"Status",-9}  {"Exam",-5}  {"Add",-5}  {"Upd",-5}  {"Skp",-5}  {"Fail",-5}  Error");
        Console.WriteLine("  " + new string('-', 130));
        foreach (var s in steps)
        {
            var dur = s.DurationMs.HasValue ? FormatMs(s.DurationMs.Value) : "—";
            var err = string.IsNullOrEmpty(s.ErrorMessage) ? "" : Trunc(s.ErrorMessage!, 50);
            Console.WriteLine($"  {Trunc(s.StepName, 40),-40}  {dur,-6}  {s.Status,-9}  {s.Examined,-5}  {s.Added,-5}  {s.Updated,-5}  {s.Skipped,-5}  {s.Failed,-5}  {err}");
        }
        return 0;
    }

    private static string FormatMs(int ms)
    {
        if (ms < 1000) return $"{ms}ms";
        if (ms < 60_000) return $"{ms / 1000.0:0.0}s";
        return $"{ms / 60_000.0:0.0}m";
    }

    private static string Trunc(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" :
        s.Length <= n ? s : s.Substring(0, n - 1) + "…";
}
