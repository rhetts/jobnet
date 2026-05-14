using System;
using System.Collections.Generic;

namespace Jobnet.Services.ApiUsage;

public interface IApiUsageTracker
{
    /// <summary>Record one API call against a provider. Returns the new count for today.
    /// Emits a warning event when today's count crosses the configured soft cap.</summary>
    int RecordCall(string provider);

    int GetTodayCount(string provider);
    int GetSoftCap(string provider);

    /// <summary>Get usage for today across all providers we have records for.</summary>
    IReadOnlyList<ApiUsageRow> GetTodayUsage();

    /// <summary>Raised once per (provider, day) the first time we cross the soft cap.</summary>
    event EventHandler<ApiUsageWarningEventArgs>? SoftCapExceeded;
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
