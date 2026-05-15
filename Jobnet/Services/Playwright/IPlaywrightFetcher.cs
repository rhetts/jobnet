using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jobnet.Services.Playwright;

public interface IPlaywrightFetcher
{
    /// <summary>Fetch a URL with full JS rendering. Returns final URL + rendered HTML + status code + observed XHR/fetch URLs.</summary>
    Task<PlaywrightFetchResult> FetchAsync(string url, CancellationToken ct = default);
}

public sealed class PlaywrightFetchResult
{
    public required string FinalUrl { get; init; }
    public required int HttpStatus { get; init; }
    public required string Html { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }

    /// <summary>All XHR/fetch URLs the browser hit during page load. Lets callers catch
    /// ATS API endpoints even when they're not visible in the static or rendered HTML.</summary>
    public IReadOnlyList<CapturedRequest> NetworkRequests { get; init; } = Array.Empty<CapturedRequest>();
}

public sealed class CapturedRequest
{
    public required string Url { get; init; }
    public required string Method { get; init; }
    public required string ResourceType { get; init; }
}
