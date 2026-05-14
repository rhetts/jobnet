using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Data.Repositories;

namespace Jobnet.Services.RateLimit;

/// <summary>
/// Enforces a per-provider minimum delay between calls. Delays are read from config
/// (`api_min_delay_ms.{provider}`). Threadsafe: serializes callers per provider via SemaphoreSlim.
/// </summary>
public sealed class RateLimiter : IRateLimiter
{
    private readonly IConfigRepository _config;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastCall = new();

    public RateLimiter(IConfigRepository config)
    {
        _config = config;
    }

    public async Task WaitAsync(string provider, CancellationToken ct = default)
    {
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
        var raw = _config.GetOrDefault($"api_min_delay_ms.{provider}", "0");
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) || ms < 0)
            return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(ms);
    }
}
