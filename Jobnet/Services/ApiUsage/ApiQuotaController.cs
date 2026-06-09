using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Jobnet.Data.Repositories;

namespace Jobnet.Services.ApiUsage;

public sealed class ApiQuotaController : IApiQuotaController
{
    private const int DefaultDelayMs = 3500;

    /// <summary>Hard ceiling on the adaptive per-minute throttle. Previously 60_000 (one call/min)
    /// which made Gemini effectively unusable after a few 429s. 10_000 = 6 RPM minimum spacing,
    /// well below any free-tier RPM cap, so we never persist a "lockout" delay.</summary>
    private const int MaxDelayMs     = 10_000;
    private const double DelayBumpFactor = 1.5;

    private readonly IConfigRepository _config;
    private readonly ConcurrentDictionary<string, QuotaDecision> _cachedDecisions = new();

    /// <summary>Transient per-provider delay bump kept in-memory only. Survives within the process
    /// but resets on restart — so a bad burst of 429s can't permanently degrade throughput. The
    /// configured <c>api_min_delay_ms.{provider}</c> remains the sacred floor.</summary>
    private readonly ConcurrentDictionary<string, int> _transientDelayMs = new();
    private readonly object _ctsLock = new();
    private CancellationTokenSource _sessionCts = new();
    private volatile bool _wasCancelledByDailyQuota;

    /// <summary>The effective min-delay for a provider, taking the in-memory bump into account.
    /// Read by <see cref="RateLimit.RateLimiter"/>; callers should prefer this over reading
    /// <c>api_min_delay_ms.{provider}</c> from config directly.</summary>
    public int GetEffectiveMinDelayMs(string provider)
    {
        var raw = _config.GetOrDefault($"api_min_delay_ms.{provider}", DefaultDelayMs.ToString(CultureInfo.InvariantCulture));
        var floor = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : DefaultDelayMs;
        return _transientDelayMs.TryGetValue(provider, out var bumped) ? Math.Max(floor, bumped) : floor;
    }

    public bool WasCancelledByDailyQuota => _wasCancelledByDailyQuota;

    public CancellationToken SessionCancellationToken
    {
        get { lock (_ctsLock) { return _sessionCts.Token; } }
    }

    public void ResetSession()
    {
        lock (_ctsLock)
        {
            _sessionCts.Dispose();
            _sessionCts = new CancellationTokenSource();
            _cachedDecisions.Clear();
            _wasCancelledByDailyQuota = false;
        }
    }

    public void CancelSession()
    {
        lock (_ctsLock)
        {
            if (!_sessionCts.IsCancellationRequested) _sessionCts.Cancel();
        }
    }

    public ApiQuotaController(IConfigRepository config)
    {
        _config = config;
    }

    public void OnPerMinuteLimit(string provider)
    {
        // Take the current effective delay (config floor OR existing in-memory bump, whichever
        // is higher) and bump 1.5x, capped at MaxDelayMs. Store the bump in-memory only — the
        // user's configured floor in api_min_delay_ms.{provider} is never overwritten, so a
        // process restart always returns to the sacred floor.
        var current = GetEffectiveMinDelayMs(provider);
        var bumped = Math.Min(MaxDelayMs, (int)(current * DelayBumpFactor));
        if (bumped > current)
            _transientDelayMs[provider] = bumped;
    }

    public Task<QuotaDecision> OnPerDayLimitAsync(string provider, string errorBody)
    {
        // Cached: user already chose for this provider in this session, return it without re-prompting.
        if (_cachedDecisions.TryGetValue(provider, out var prev))
            return Task.FromResult(prev);

        // Run the dialog on the UI thread. Background callers awaiting this will park until the
        // user clicks Yes or No.
        var app = Application.Current;
        if (app is null)
        {
            // No UI context (CLI mode) — treat as Continue so the existing one-shot retry path runs.
            return Task.FromResult(QuotaDecision.Continue);
        }

        return app.Dispatcher.InvokeAsync(() =>
        {
            var summary = Truncate(errorBody, 700);
            var message =
                $"{provider} daily quota appears exhausted.\n\n" +
                $"Server replied:\n{summary}\n\n" +
                $"Click Yes to keep trying (the cap may reset shortly), or No to cancel the current run. " +
                $"Either choice is remembered for the rest of the session.";

            var result = MessageBox.Show(
                Application.Current.MainWindow,
                message,
                $"{provider} — daily quota",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            var decision = result == MessageBoxResult.Yes ? QuotaDecision.Continue : QuotaDecision.Cancel;
            _cachedDecisions[provider] = decision;
            if (decision == QuotaDecision.Cancel)
            {
                // Signal every batch loop holding our token to unwind. Without this, only the
                // single AI call that triggered the dialog throws — the outer foreach happily
                // moves to the next item and hits the same exhausted provider all over again.
                _wasCancelledByDailyQuota = true;
                lock (_ctsLock) { _sessionCts.Cancel(); }
            }
            return decision;
        }).Task;
    }

    private static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" :
        s.Length <= n ? s : s.Substring(0, n) + "...";
}
