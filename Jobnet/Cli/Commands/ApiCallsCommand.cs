using System;
using System.Linq;
using Dapper;
using Jobnet.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

/// <summary>
/// Recent rows from <c>api_call_log</c>. Useful for diagnosing 429s and rate-limit errors —
/// shows the gap between calls so you can spot RPM bursts that the daily total alone hides.
/// </summary>
public sealed class ApiCallsCommand : ICliCommand
{
    public string Name => "api-calls";
    public string Description =>
        "Show recent api_call_log rows. Usage: api-calls [<provider>] [--limit N] [--errors-only]";

    public int Run(string[] args, IServiceProvider services)
    {
        var connections = services.GetRequiredService<IDbConnectionFactory>();

        string? provider = null;
        var limit = 30;
        var errorsOnly = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--limit" when i + 1 < args.Length: int.TryParse(args[++i], out limit); break;
                case "--errors-only": errorsOnly = true; break;
                default:
                    if (!args[i].StartsWith("--")) provider = args[i];
                    break;
            }
        }

        using var conn = connections.Open();
        var where = "WHERE 1=1";
        if (provider is not null) where += " AND provider = @provider";
        if (errorsOnly) where += " AND (status_code IS NOT NULL AND status_code >= 400)";

        var rows = conn.Query<Row>($@"
            SELECT id, provider, called_at AS CalledAt,
                   input_tokens AS InputTokens, output_tokens AS OutputTokens,
                   status_code AS StatusCode, error_message AS ErrorMessage
            FROM api_call_log
            {where}
            ORDER BY called_at DESC LIMIT @limit",
            new { provider, limit }).ToList();

        if (rows.Count == 0) { Console.WriteLine("(no rows)"); return 0; }

        // Sort by time ascending for display so gap calculation reads naturally.
        rows.Reverse();

        Console.WriteLine($"{"ID",-7}  {"Called (UTC)",-23}  {"Gap",-7}  {"Provider",-20}  {"In",-5}  {"Out",-5}  {"Status",-7}  Error");
        Console.WriteLine(new string('-', 110));

        DateTime? prev = null;
        foreach (var r in rows)
        {
            var t = DateTime.Parse(r.CalledAt).ToUniversalTime();
            var gap = prev is null ? "—" : FormatGap(t - prev.Value);
            prev = t;
            var status = r.StatusCode is null ? "—" : r.StatusCode.ToString();
            var err = string.IsNullOrEmpty(r.ErrorMessage) ? "" :
                      r.ErrorMessage!.Length <= 60 ? r.ErrorMessage : r.ErrorMessage.Substring(0, 57) + "...";
            Console.WriteLine($"{r.Id,-7}  {t:yyyy-MM-dd HH:mm:ss}    {gap,-7}  {Trunc(r.Provider, 20),-20}  {r.InputTokens,-5}  {r.OutputTokens,-5}  {status,-7}  {err}");
        }
        return 0;
    }

    private static string FormatGap(TimeSpan g)
    {
        if (g.TotalMilliseconds < 1000) return $"{g.TotalMilliseconds:0}ms";
        if (g.TotalSeconds   < 60)      return $"{g.TotalSeconds:0.0}s";
        if (g.TotalMinutes   < 60)      return $"{g.TotalMinutes:0.0}m";
        return $"{g.TotalHours:0.0}h";
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";

    private sealed class Row
    {
        public long Id { get; set; }
        public string Provider { get; set; } = "";
        public string CalledAt { get; set; } = "";
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int? StatusCode { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
