using System;
using Jobnet.Services.ApiUsage;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class UsageCommand : ICliCommand
{
    public string Name => "usage";
    public string Description => "Show today's API call counts vs. soft caps";

    public int Run(string[] args, IServiceProvider services)
    {
        var tracker = services.GetRequiredService<IApiUsageTracker>();
        var rows = tracker.GetTodayUsage();

        Console.WriteLine($"API usage today ({DateTime.UtcNow:yyyy-MM-dd} UTC):");
        Console.WriteLine();
        Console.WriteLine($"{"Provider",-28}  {"Count",6}  {"Cap",6}  {"Usage",-12}  Status");
        Console.WriteLine(new string('-', 75));

        if (rows.Count == 0)
        {
            Console.WriteLine("(no API calls recorded today)");
            return 0;
        }

        foreach (var r in rows)
        {
            var pct = r.SoftCap > 0 ? (double)r.Count / r.SoftCap * 100 : 0;
            var bar = MakeBar(pct);
            var status = r.SoftCap <= 0 ? "no cap set"
                       : r.Count >= r.SoftCap ? "OVER CAP"
                       : pct >= 80 ? "nearing cap"
                       : "ok";
            Console.WriteLine($"{r.Provider,-28}  {r.Count,6}  {r.SoftCap,6}  {bar,-12}  {status}");
        }
        return 0;
    }

    private static string MakeBar(double pct)
    {
        pct = Math.Clamp(pct, 0, 100);
        var filled = (int)Math.Round(pct / 10);
        return new string('█', filled) + new string('·', 10 - filled);
    }
}
