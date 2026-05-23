using System;
using System.Collections.Generic;

namespace Jobnet.Services.ApiUsage;

public interface IApiUsageTracker
{
    /// <summary>Record one API call against a provider. Returns the new count for today.
    /// Optional tokens (for AI providers) are stored alongside the call timestamp.
    /// Emits a warning event when today's count crosses the configured soft cap.</summary>
    int RecordCall(string provider, int inputTokens = 0, int outputTokens = 0);

    /// <summary>After a successful AI response, write token counts to the most recent log row
    /// for the provider (filled in by a prior RecordCall). Lets us count attempts AND tokens.</summary>
    void UpdateLastCallTokens(string provider, int inputTokens, int outputTokens);

    /// <summary>Record the HTTP outcome of the most recent RecordCall row for this provider.
    /// Call on BOTH success and failure paths so we can later distinguish 200s from 429s/5xx
    /// in api_call_log. <paramref name="errorMessage"/> should be a short body excerpt for failures.</summary>
    void RecordCallOutcome(string provider, int statusCode, string? errorMessage = null);

    int GetTodayCount(string provider);
    int GetSoftCap(string provider);

    /// <summary>Get usage for today across all providers we have records for.</summary>
    IReadOnlyList<ApiUsageRow> GetTodayUsage();

    /// <summary>Snapshot of every limit dimension we care about for one provider.</summary>
    ApiUsageSnapshot GetSnapshot(string provider);

    /// <summary>Snapshots for every provider we have ever seen a call from (today or older).</summary>
    IReadOnlyList<ApiUsageSnapshot> GetAllSnapshots();

    /// <summary>Raised once per (provider, day) the first time we cross the soft cap.</summary>
    event EventHandler<ApiUsageWarningEventArgs>? SoftCapExceeded;
}

public sealed class ApiUsageSnapshot
{
    public required string Provider { get; init; }
    public int Rpd { get; init; }               // requests today
    public int RpdCap { get; init; }
    public int Rpm { get; init; }               // requests in last 60 seconds
    public int RpmCap { get; init; }
    public int Tpm { get; init; }               // tokens in last 60 seconds (input+output)
    public int TpmCap { get; init; }
    public int TokensToday { get; init; }
    public int TokensTodayCap { get; init; }    // tokens-per-day cap (Groq enforces this)
    public DateTime? LastCallUtc { get; init; }
}

public sealed class ApiUsageRow
{
    public required string Provider { get; init; }
    public required string Date { get; init; }
    public required int Count { get; init; }
    public required int SoftCap { get; init; }
    public DateTime LastCall { get; init; }
}

public sealed class ApiUsageWarningEventArgs : EventArgs
{
    public required string Provider { get; init; }
    public required int Count { get; init; }
    public required int SoftCap { get; init; }
}
