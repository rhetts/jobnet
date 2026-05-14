using System.Threading;
using System.Threading.Tasks;

namespace Jobnet.Services.RateLimit;

public interface IRateLimiter
{
    /// <summary>Block until the next call to `provider` is allowed by its configured min-delay.</summary>
    Task WaitAsync(string provider, CancellationToken ct = default);

    /// <summary>How long the next WaitAsync call would block. For diagnostics.</summary>
    System.TimeSpan PreviewDelay(string provider);
}
