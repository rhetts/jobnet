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
    private const int MaxDelayMs     = 60_000;
    private const double DelayBumpFactor = 1.5;

    private readonly IConfigRepository _config;
    private readonly ConcurrentDictionary<string, QuotaDecision> _cachedDecisions = new();
    private readonly object _ctsLock = new();
    private CancellationTokenSource _sessionCts = new();

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
        var key = $"api_min_delay_ms.{provider}";
        var raw = _config.GetOrDefault(key, DefaultDelayMs.ToString(CultureInfo.InvariantCulture));
        var current = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : DefaultDelayMs;
        var bumped = Math.Min(MaxDelayMs, (int)(current * DelayBumpFactor));
        if (bumped > current)
        {
            _config.Set(key, bumped.ToString(CultureInfo.InvariantCulture));
        }
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
                lock (_ctsLock) { _sessionCts.Cancel(); }
            }
            return decision;
        }).Task;
    }

    private static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" :
        s.Length <= n ? s : s.Substring(0, n) + "...";
}
