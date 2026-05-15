using System;

namespace Jobnet.Services.RateLimit;

/// <summary>Thrown by the rate limiter when today's call count has reached the soft cap for a provider.
/// Caller should stop calling that provider until UTC midnight (or raise the cap via config).</summary>
public sealed class SoftCapExceededException : Exception
{
    public string Provider { get; }
    public int Count { get; }
    public int SoftCap { get; }

    public SoftCapExceededException(string provider, int count, int softCap)
        : base($"Daily soft cap reached for '{provider}': {count}/{softCap}. Raise via `config-set api_soft_cap.{provider} <new>` or wait for UTC reset.")
    {
        Provider = provider;
        Count = count;
        SoftCap = softCap;
    }
}
