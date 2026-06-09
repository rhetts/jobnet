using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dapper;
using Jobnet.Data;
using Jobnet.Data.Repositories;

namespace Jobnet.Services.ApiUsage;

public sealed class ApiUsageTracker : IApiUsageTracker
{
    private readonly IDbConnectionFactory _connections;
    private readonly IConfigRepository _config;
    private readonly ConcurrentDictionary<string, bool> _warnedToday = new();

    public event EventHandler<ApiUsageWarningEventArgs>? SoftCapExceeded;
    public event EventHandler<ApiCallRecordedEventArgs>? CallRecorded;

    public ApiUsageTracker(IDbConnectionFactory connections, IConfigRepository config)
    {
        _connections = connections;
        _config = config;
    }

    public int RecordCall(string provider, int inputTokens = 0, int outputTokens = 0)
    {
        var date = TodayUtc();
        using var conn = _connections.Open();
        var now = DateTime.UtcNow.ToString("o");

        // Daily roll-up (preserves the existing API surface).
        conn.Execute(@"
            INSERT INTO api_usage (provider, date, count, last_call)
            VALUES (@provider, @date, 1, @now)
            ON CONFLICT(provider, date) DO UPDATE
                SET count = count + 1, last_call = excluded.last_call",
            new { provider, date, now });

        // Per-call log row for RPM/TPM analytics.
        conn.Execute(@"
            INSERT INTO api_call_log (provider, called_at, input_tokens, output_tokens)
            VALUES (@provider, @now, @inputTokens, @outputTokens)",
            new { provider, now, inputTokens, outputTokens });

        var count = conn.ExecuteScalar<int>(
            "SELECT count FROM api_usage WHERE provider = @provider AND date = @date",
            new { provider, date });

        var cap = GetSoftCap(provider);
        if (cap > 0 && count >= cap)
        {
            var key = $"{provider}|{date}";
            if (_warnedToday.TryAdd(key, true))
            {
                SoftCapExceeded?.Invoke(this, new ApiUsageWarningEventArgs
                {
                    Provider = provider, Count = count, SoftCap = cap
                });
            }
        }

        // Notify live UI counters. Subscribers must marshal to UI thread themselves.
        CallRecorded?.Invoke(this, new ApiCallRecordedEventArgs
        {
            Provider = provider,
            CountToday = count,
        });

        return count;
    }

    public int GetTodayCount(string provider)
    {
        using var conn = _connections.Open();
        return conn.ExecuteScalar<int?>(
            "SELECT count FROM api_usage WHERE provider = @provider AND date = @date",
            new { provider, date = TodayUtc() }) ?? 0;
    }

    public int GetSoftCap(string provider)
    {
        var raw = _config.GetOrDefault($"api_soft_cap.{provider}", "");
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    public IReadOnlyList<ApiUsageRow> GetTodayUsage()
    {
        using var conn = _connections.Open();
        var rows = conn.Query<(string Provider, string Date, int Count, string LastCall)>(
            "SELECT provider, date, count, last_call FROM api_usage WHERE date = @date ORDER BY count DESC, provider",
            new { date = TodayUtc() }).ToList();

        return rows.Select(r => new ApiUsageRow
        {
            Provider = r.Provider,
            Date = r.Date,
            Count = r.Count,
            SoftCap = GetSoftCap(r.Provider),
            LastCall = DateTime.Parse(r.LastCall).ToUniversalTime()
        }).ToList();
    }

    public void UpdateLastCallTokens(string provider, int inputTokens, int outputTokens)
    {
        using var conn = _connections.Open();
        conn.Execute(@"
            UPDATE api_call_log
            SET input_tokens = @inputTokens, output_tokens = @outputTokens
            WHERE id = (SELECT id FROM api_call_log
                        WHERE provider = @provider
                        ORDER BY id DESC LIMIT 1)",
            new { provider, inputTokens, outputTokens });
    }

    public void RecordCallOutcome(string provider, int statusCode, string? errorMessage = null)
    {
        // Trim the error body so a giant HTML page doesn't bloat the row. 500 chars is enough
        // to read the JSON error envelope returned by every provider we hit.
        if (errorMessage is { Length: > 500 }) errorMessage = errorMessage.Substring(0, 500);
        using var conn = _connections.Open();
        conn.Execute(@"
            UPDATE api_call_log
            SET status_code = @statusCode, error_message = @errorMessage
            WHERE id = (SELECT id FROM api_call_log
                        WHERE provider = @provider
                        ORDER BY id DESC LIMIT 1)",
            new { provider, statusCode, errorMessage });
    }

    public ApiUsageSnapshot GetSnapshot(string provider)
    {
        using var conn = _connections.Open();
        var date = TodayUtc();
        var cutoff = DateTime.UtcNow.AddMinutes(-1).ToString("o");

        var rpd = conn.ExecuteScalar<int?>(
            "SELECT count FROM api_usage WHERE provider = @provider AND date = @date",
            new { provider, date }) ?? 0;

        var lastCall = conn.ExecuteScalar<string?>(
            "SELECT last_call FROM api_usage WHERE provider = @provider AND date = @date",
            new { provider, date });

        var rpm = conn.ExecuteScalar<int?>(
            "SELECT COUNT(*) FROM api_call_log WHERE provider = @provider AND called_at >= @cutoff",
            new { provider, cutoff }) ?? 0;

        var tpm = conn.ExecuteScalar<int?>(@"
            SELECT COALESCE(SUM(input_tokens + output_tokens), 0)
            FROM api_call_log WHERE provider = @provider AND called_at >= @cutoff",
            new { provider, cutoff }) ?? 0;

        var tokensToday = conn.ExecuteScalar<int?>(@"
            SELECT COALESCE(SUM(input_tokens + output_tokens), 0)
            FROM api_call_log WHERE provider = @provider AND called_at >= @startOfDay",
            new { provider, startOfDay = DateTime.UtcNow.Date.ToString("o") }) ?? 0;

        return new ApiUsageSnapshot
        {
            Provider = provider,
            Rpd = rpd,
            RpdCap = GetSoftCap(provider),
            Rpm = rpm,
            RpmCap = GetIntCap($"api_rpm_cap.{provider}"),
            Tpm = tpm,
            TpmCap = GetIntCap($"api_tpm_cap.{provider}"),
            TokensToday = tokensToday,
            TokensTodayCap = GetIntCap($"api_tpd_cap.{provider}"),
            LastCallUtc = string.IsNullOrEmpty(lastCall) ? null : DateTime.Parse(lastCall).ToUniversalTime(),
        };
    }

    public IReadOnlyList<ApiUsageSnapshot> GetAllSnapshots()
    {
        // Union of providers we've ever seen calls from in api_usage (today or older).
        using var conn = _connections.Open();
        var providers = conn.Query<string>(
            "SELECT DISTINCT provider FROM api_usage ORDER BY provider").ToList();
        return providers.Select(GetSnapshot).ToList();
    }

    private int GetIntCap(string key)
    {
        var raw = _config.GetOrDefault(key, "");
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static string TodayUtc() => DateTime.UtcNow.ToString("yyyy-MM-dd");
}
