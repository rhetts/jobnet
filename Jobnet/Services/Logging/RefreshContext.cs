using System.Threading;

namespace Jobnet.Services.Logging;

/// <summary>
/// AsyncLocal carrier so deep call-sites (ApiUsageTracker, GeminiClient, etc.) can stamp
/// the current (run_id, company_id) onto telemetry rows without every method signature
/// having to thread the values through. JobRefresher sets the scope around each company
/// refresh; everything underneath that just reads <see cref="Current"/>.
///
/// AsyncLocal — not ThreadLocal — because the refresh path is async and awaits hop threads.
/// </summary>
public static class RefreshContext
{
    private static readonly AsyncLocal<Scope?> _current = new();

    public static Scope? Current => _current.Value;

    /// <summary>Open a scope that will be visible to all awaited work inside the <c>using</c>.
    /// Nested scopes restore the outer scope on Dispose, so a nested refresh (rare but possible
    /// during detect-ats inside refresh-jobs) doesn't accidentally tag its API calls with the
    /// inner company id forever.</summary>
    public static IDisposable BeginScope(long? runId, int? companyId)
    {
        var prev = _current.Value;
        _current.Value = new Scope(runId, companyId);
        return new ScopeReset(prev);
    }

    public sealed record Scope(long? RunId, int? CompanyId);

    private sealed class ScopeReset : IDisposable
    {
        private readonly Scope? _prev;
        public ScopeReset(Scope? prev) { _prev = prev; }
        public void Dispose() => _current.Value = _prev;
    }
}
