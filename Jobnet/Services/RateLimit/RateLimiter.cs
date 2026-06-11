using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Data.Repositories;
using Jobnet.Services.ApiUsage;

namespace Jobnet.Services.RateLimit;

/// <summary>
/// Enforces three things per provider:
///   (a) Per-day soft cap (<c>api_soft_cap.{provider}</c>) — throws on breach.
///   (b) Minimum delay between calls (<c>api_min_delay_ms.{provider}</c>, with transient
///       adaptive bumps from <see cref="IApiQuotaController"/>). The floor.
///   (c) Per-minute sliding window (<c>api_rpm_cap.{provider}</c>) — a true 60-second
///       window of recent call timestamps. Blocks until the oldest call rolls off when at
///       cap, so a burst can use full capacity instead of being artificially spaced.
///
/// The min-delay floor and the RPM ceiling are both enforced; whichever is tighter at the
/// moment wins. For Gemini at 6500ms floor + 10 RPM cap, the floor dominates (9.2 RPM
/// effective) — but once we drop the floor (or for providers with no floor) the RPM gate
/// kicks in cleanly.
///
/// Threadsafe: serializes callers per provider via SemaphoreSlim, so each provider's window
/// is mutated by exactly one in-flight WaitAsync at a time.
/// </summary>
public sealed class RateLimiter : IRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    /// <summary>Small extra delay added when sleeping for the RPM gate, so the call that wakes
    /// up doesn't race the 60-second cutoff and still see N items in the window.</summary>
    private static readonly TimeSpan RpmSleepMargin = TimeSpan.FromMilliseconds(100);

    private readonly IConfigRepository _config;
    private readonly IApiUsageTracker _usage;
    private readonly IApiQuotaController _quota;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastCall = new();

    /// <summary>Rolling window of recent call start times per provider. Entries older than
    /// <see cref="Window"/> are pruned on each WaitAsync. The queue is mutated only while the
    /// per-provider semaphore is held.</summary>
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _recentCalls = new();

    public RateLimiter(IConfigRepository config, IApiUsageTracker usage, IApiQuotaController quota)
    {
        _config = config;
        _usage = usage;
        _quota = quota;
    }

    public async Task WaitAsync(string provider, CancellationToken ct = default)
    {
        // (a) Hard stop on today's soft cap.
        var cap = _usage.GetSoftCap(provider);
        if (cap > 0)
        {
            var count = _usage.GetTodayCount(provider);
            if (count >= cap) throw new SoftCapExceededException(provider, count, cap);
        }

        var gate = _gates.GetOrAdd(provider, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // (b) Min-delay floor between consecutive calls.
            var delay = GetMinDelay(provider);
            if (delay > TimeSpan.Zero && _lastCall.TryGetValue(provider, out var last))
            {
                var remaining = delay - (DateTime.UtcNow - last);
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, ct).ConfigureAwait(false);
            }

            // (c) RPM sliding window. Blocks (rather than throws) so the caller doesn't need
            // to know about the gate — they just await WaitAsync and get a call slot when one
            // is available. Cap = 0 disables this dimension entirely.
            var rpmCap = GetRpmCap(provider);
            if (rpmCap > 0)
            {
                var window = _recentCalls.GetOrAdd(provider, _ => new Queue<DateTime>());
                while (true)
                {
                    PruneWindow(window);
                    if (window.Count < rpmCap) break;
                    var oldest = window.Peek();
                    var wakeAt = oldest + Window + RpmSleepMargin;
                    var sleep = wakeAt - DateTime.UtcNow;
                    if (sleep <= TimeSpan.Zero) { PruneWindow(window); break; }
                    await Task.Delay(sleep, ct).ConfigureAwait(false);
                }
                window.Enqueue(DateTime.UtcNow);
            }

            _lastCall[provider] = DateTime.UtcNow;
        }
        finally
        {
            gate.Release();
        }
    }

    public TimeSpan PreviewDelay(string provider)
    {
        var floor = TimeSpan.Zero;
        var delay = GetMinDelay(provider);
        if (delay > TimeSpan.Zero && _lastCall.TryGetValue(provider, out var last))
        {
            var remaining = delay - (DateTime.UtcNow - last);
            if (remaining > floor) floor = remaining;
        }

        var rpmCap = GetRpmCap(provider);
        if (rpmCap > 0 && _recentCalls.TryGetValue(provider, out var window))
        {
            // Don't mutate during preview; compute the headroom from a snapshot.
            DateTime[] snapshot;
            lock (window) snapshot = window.ToArray();
            var live = 0; DateTime oldest = DateTime.MaxValue;
            var cutoff = DateTime.UtcNow - Window;
            foreach (var t in snapshot)
            {
                if (t < cutoff) continue;
                live++;
                if (t < oldest) oldest = t;
            }
            if (live >= rpmCap && oldest != DateTime.MaxValue)
            {
                var sleep = oldest + Window + RpmSleepMargin - DateTime.UtcNow;
                if (sleep > floor) floor = sleep;
            }
        }
        return floor < TimeSpan.Zero ? TimeSpan.Zero : floor;
    }

    private static void PruneWindow(Queue<DateTime> window)
    {
        var cutoff = DateTime.UtcNow - Window;
        while (window.Count > 0 && window.Peek() < cutoff) window.Dequeue();
    }

    private TimeSpan GetMinDelay(string provider)
    {
        // Consult the controller — gives us the configured floor combined with any in-memory
        // adaptive bump from past 429s. The bump is transient (resets on restart) so we never
        // permanently degrade throughput the way the old persisted-bump approach did.
        var ms = _quota.GetEffectiveMinDelayMs(provider);
        if (ms < 0) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>The 60-second cap for <paramref name="provider"/>. Zero (or unset) disables the
    /// RPM gate for this provider — the min-delay floor still applies.</summary>
    private int GetRpmCap(string provider)
    {
        var raw = _config.GetOrDefault($"api_rpm_cap.{provider}", "0");
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 0;
    }
}
