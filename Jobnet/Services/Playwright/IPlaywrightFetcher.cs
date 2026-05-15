using System.Threading;
using System.Threading.Tasks;

namespace Jobnet.Services.Playwright;

public interface IPlaywrightFetcher
{
    /// <summary>Fetch a URL with full JS rendering. Returns final URL + rendered HTML + status code.</summary>
    Task<PlaywrightFetchResult> FetchAsync(string url, CancellationToken ct = default);
}

public sealed class PlaywrightFetchResult
{
    public required string FinalUrl { get; init; }
    public required int HttpStatus { get; init; }
    public required string Html { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }
}
