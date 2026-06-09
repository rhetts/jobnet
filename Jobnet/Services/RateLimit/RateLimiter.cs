using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Data.Repositories;
using Jobnet.Services.ApiUsage;

namespace Jobnet.Services.RateLimit;

/// <summary>
/// Enforces (a) per-provider minimum delay between calls and (b) per-day soft cap.
/// Throws SoftCapExceededException if today's count has already reached the cap.
/// Delays + caps both read from config: api_min_delay_ms.{provider}, api_soft_cap.{provider}.
/// Threadsafe: serializes callers per provider via SemaphoreSlim.
/// </summary>
public sealed class RateLimiter : IRateLimiter
{
    private readonly IConfigRepository _config;
    private readonly IApiUsageTracker _usage;
    private readonly IApiQuotaController _quota;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastCall = new();

    public RateLimiter(IConfigRepository config, IApiUsageTracker usage, IApiQuotaController quota)
    {
        _config = config;
        _usage = usage;
        _quota = quota;
    }

    public async Task WaitAsync(string provider, CancellationToken ct = default)
    {
        // Hard stop if we've already hit today's soft cap for this provider.
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
            var delay = GetMinDelay(provider);
            if (delay <= TimeSpan.Zero)
            {
                _lastCall[provider] = DateTime.UtcNow;
                return;
            }

            if (_lastCall.TryGetValue(provider, out var last))
            {
                var waited = DateTime.UtcNow - last;
                var remaining = delay - waited;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, ct).ConfigureAwait(false);
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
        var delay = GetMinDelay(provider);
        if (delay <= TimeSpan.Zero) return TimeSpan.Zero;
        if (!_lastCall.TryGetValue(provider, out var last)) return TimeSpan.Zero;
        var remaining = delay - (DateTime.UtcNow - last);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
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
}
