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

    public ApiUsageTracker(IDbConnectionFactory connections, IConfigRepository config)
    {
        _connections = connections;
        _config = config;
    }

    public int RecordCall(string provider)
    {
        var date = TodayUtc();
        using var conn = _connections.Open();
        var now = DateTime.UtcNow.ToString("o");
        conn.Execute(@"
            INSERT INTO api_usage (provider, date, count, last_call)
            VALUES (@provider, @date, 1, @now)
            ON CONFLICT(provider, date) DO UPDATE
                SET count = count + 1, last_call = excluded.last_call",
            new { provider, date, now });

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

    private static string TodayUtc() => DateTime.UtcNow.ToString("yyyy-MM-dd");
}
