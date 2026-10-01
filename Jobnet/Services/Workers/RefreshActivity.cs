using System;
using Dapper;
using Jobnet.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Services.Workers;

/// <summary>
/// "Is a job refresh running right now?" — checked by the background workers at the top of each
/// cycle so they sit out while a scan is in progress. The workers and the refresh share the local
/// LLama model (one inference at a time), and a backlog of 60s resume-match batches starved the
/// refresh of it: one company took 30+ minutes. Pausing the workers keeps the model, the Groq
/// rate limit and the DB free for the scan; the queue simply drains once it's done.
///
/// Reads <c>run_log</c> rather than an in-process flag so a refresh started from the CLI (a
/// separate process) pauses the GUI's workers too. Rows left 'running' by a crash are marked
/// 'interrupted' on the next startup; the 6-hour window is a backstop in case that never happens.
/// </summary>
public static class RefreshActivity
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(6);

    public static bool IsRefreshRunning(IServiceProvider sp)
    {
        try
        {
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().Open();
            var cutoff = DateTime.UtcNow.Subtract(StaleAfter).ToString("o");
            return conn.ExecuteScalar<long>(@"
                SELECT COUNT(*) FROM run_log
                WHERE run_type IN ('refresh_jobs', 'refresh_existing')
                  AND status = 'running'
                  AND started_at >= @cutoff", new { cutoff }) > 0;
        }
        catch
        {
            // Can't tell — don't block the workers on a diagnostic query failing.
            return false;
        }
    }
}
