using System;
using System.Linq;
using Dapper;
using Jobnet.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

/// <summary>
/// Surfaces native-ATS companies whose *most recent* refresh attempt failed — the report that
/// should have existed before a manual audit found 13 companies quietly broken for weeks (some
/// via the same wrong-subdomain-returns-real-but-irrelevant-data failure mode: a 200 OK for the
/// wrong tenant, not an HTTP error). The existing auto-clear-stale-slug mechanism in JobRefresher
/// self-heals *eventually*, but only after 2 consecutive failures — and with hundreds of
/// companies competing for a handful of scan slots per run (native-only + skip-recent
/// throttling), "eventually" can be a long time. This is the standing check in between.
/// </summary>
public sealed class AtsHealthCommand : ICliCommand
{
    public string Name => "ats-health";
    public string Description => "List native-ATS companies whose most recent refresh attempt failed.";

    public int Run(string[] args, IServiceProvider services)
    {
        var connections = services.GetRequiredService<IDbConnectionFactory>();
        using var conn = connections.Open();

        var rows = conn.Query<Row>(@"
            SELECT c.id AS Id, c.name AS Name, c.domain AS Domain, c.ats_type AS AtsType,
                   c.ats_slug AS AtsSlug, ra.result AS Result, ra.http_status AS HttpStatus,
                   ra.error_message AS ErrorMessage, ra.started_at AS StartedAt
            FROM companies c
            JOIN refresh_attempt ra ON ra.company_id = c.id AND ra.stage = 'ats_api'
            WHERE c.is_active = 1
              AND c.ats_type IS NOT NULL AND c.ats_type != ''
              -- ats_slug (not just ats_type) must still be set — ClearAtsSlug deliberately keeps
              -- ats_type as a hint for the next detect-ats while nulling ats_slug, and a null
              -- slug already means RefreshOneAsync won't take the native path at all, so a
              -- company in that state isn't currently broken, just pending re-detection.
              AND c.ats_slug IS NOT NULL AND c.ats_slug != ''
              AND ra.id = (
                  SELECT ra2.id FROM refresh_attempt ra2
                  WHERE ra2.company_id = c.id AND ra2.stage = 'ats_api'
                  ORDER BY ra2.started_at DESC LIMIT 1
              )
              AND ra.result NOT IN ('success', 'empty')
            ORDER BY c.name").ToList();

        if (rows.Count == 0)
        {
            Console.WriteLine("No native-ATS companies with a failing most-recent attempt. Clean.");
            return 0;
        }

        Console.WriteLine($"{"Company",-28}  {"Domain",-24}  {"ATS",-10}  {"Result",-16}  {"HTTP",-5}  Last attempt");
        Console.WriteLine(new string('-', 110));
        foreach (var r in rows)
        {
            Console.WriteLine($"{Trunc(r.Name, 28),-28}  {Trunc(r.Domain, 24),-24}  {Trunc(r.AtsType, 10),-10}  " +
                               $"{r.Result,-16}  {r.HttpStatus?.ToString() ?? "",-5}  {r.StartedAt:yyyy-MM-dd HH:mm}");
        }
        Console.WriteLine();
        Console.WriteLine($"{rows.Count} compan{(rows.Count == 1 ? "y" : "ies")} with a broken native ATS integration.");
        return 1;
    }

    private static string Trunc(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    private sealed class Row
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Domain { get; set; } = "";
        public string AtsType { get; set; } = "";
        public string? AtsSlug { get; set; }
        public string Result { get; set; } = "";
        public int? HttpStatus { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime StartedAt { get; set; }
    }
}
