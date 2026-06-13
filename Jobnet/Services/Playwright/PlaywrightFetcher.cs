using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Playwright;
using Jobnet.Data;
using Jobnet.Services.ApiUsage;
using Jobnet.Services.RateLimit;

namespace Jobnet.Services.Playwright;

/// <summary>
/// Headless Chromium fetcher. Lazily initializes a shared browser instance on first use.
/// Throws if Chromium isn't installed — call EnsureBrowserInstalledAsync at startup to handle that.
/// </summary>
public sealed class PlaywrightFetcher : IPlaywrightFetcher, IAsyncDisposable
{
    public const string Provider = "playwright_fetch";

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly IApiUsageTracker _usage;
    private readonly IRateLimiter _rateLimiter;
    private readonly IDbConnectionFactory _connections;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private bool _disposed;

    public PlaywrightFetcher(IApiUsageTracker usage, IRateLimiter rateLimiter, IDbConnectionFactory connections)
    {
        _usage = usage;
        _rateLimiter = rateLimiter;
        _connections = connections;
    }

    public async Task<PlaywrightFetchResult> FetchAsync(string url, CancellationToken ct = default)
    {
        await _rateLimiter.WaitAsync(Provider, ct);
        _usage.RecordCall(Provider);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        PlaywrightFetchResult? result = null;
        try
        {
            result = await FetchInternalAsync(url, ct);
            return result;
        }
        finally
        {
            sw.Stop();
            // Persist a forensic record so "why did this AI extract get 0 jobs?" becomes one SQL
            // query, not a guess. Includes the captured network-request count which is how we
            // tell SPA renders from static pages without re-fetching. Stamped with company_id /
            // run_id from the AsyncLocal scope if present.
            try
            {
                var ctx = Jobnet.Services.Logging.RefreshContext.Current;
                using var conn = _connections.Open();
                var html = result?.Html ?? "";
                var sha = html.Length > 0
                    ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(html))).Substring(0, 32)
                    : null;
                conn.Execute(@"
                    INSERT INTO page_fetches (company_id, url, fetched_at, http_status,
                                              content_sha256, success, error_message)
                    VALUES (@companyId, @url, @fetchedAt, @httpStatus,
                            @sha, @success, @error)",
                    new {
                        companyId = ctx?.CompanyId,
                        url,
                        fetchedAt = DateTime.UtcNow.ToString("o"),
                        httpStatus = result?.HttpStatus ?? 0,
                        sha,
                        success = (result?.Success ?? false) ? 1 : 0,
                        error = result?.Error,
                    });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[page_fetches] insert failed: {ex.Message}");
            }
        }
    }

    private async Task<PlaywrightFetchResult> FetchInternalAsync(string url, CancellationToken ct)
    {
        var browser = await EnsureBrowserAsync(ct);
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/Jobnet/0.7 Safari/537.36",
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
        });
        try
        {
            var page = await context.NewPageAsync();
            var requests = new System.Collections.Generic.List<CapturedRequest>();
            var requestsLock = new object();

            // Capture XHR + fetch URLs the page makes — useful for catching ATS API endpoints (Greenhouse, Lever, etc.)
            // even when nothing about them appears in the static or rendered HTML.
            page.Request += (_, req) =>
            {
                var rt = req.ResourceType;
                if (rt is not ("xhr" or "fetch" or "document")) return;
                lock (requestsLock)
                {
                    if (requests.Count < 500)
                        requests.Add(new CapturedRequest { Url = req.Url, Method = req.Method, ResourceType = rt });
                }
            };

            try
            {
                var response = await page.GotoAsync(url, new PageGotoOptions
                {
                    Timeout = 30_000,
                    WaitUntil = WaitUntilState.NetworkIdle,
                });
                var status = response?.Status ?? 0;
                var finalUrl = page.Url;
                var html = await page.ContentAsync();

                return new PlaywrightFetchResult
                {
                    FinalUrl = finalUrl,
                    HttpStatus = status,
                    Html = html,
                    Success = status > 0 && status < 400,
                    NetworkRequests = SnapshotRequests(requests, requestsLock),
                };
            }
            catch (TimeoutException)
            {
                // Page didn't reach NetworkIdle; grab whatever HTML we have.
                try
                {
                    var html = await page.ContentAsync();
                    return new PlaywrightFetchResult
                    {
                        FinalUrl = page.Url,
                        HttpStatus = 0,
                        Html = html,
                        Success = !string.IsNullOrEmpty(html),
                        Error = "Page did not reach network-idle within 30s; returned partial render",
                        NetworkRequests = SnapshotRequests(requests, requestsLock),
                    };
                }
                catch
                {
                    return new PlaywrightFetchResult { FinalUrl = url, HttpStatus = 0, Html = "", Success = false, Error = "Timeout, no content", NetworkRequests = SnapshotRequests(requests, requestsLock) };
                }
            }
            catch (Exception ex)
            {
                return new PlaywrightFetchResult { FinalUrl = url, HttpStatus = 0, Html = "", Success = false, Error = ex.Message, NetworkRequests = SnapshotRequests(requests, requestsLock) };
            }
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    private static IReadOnlyList<CapturedRequest> SnapshotRequests(System.Collections.Generic.List<CapturedRequest> list, object gate)
    {
        lock (gate) return list.ToArray();
    }

    private async Task<IBrowser> EnsureBrowserAsync(CancellationToken ct)
    {
        if (_browser is not null && _browser.IsConnected) return _browser;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_browser is not null && _browser.IsConnected) return _browser;
            _playwright ??= await Microsoft.Playwright.Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Timeout = 30_000,
            });
            return _browser;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>Run the Playwright CLI to ensure Chromium is installed. Idempotent.</summary>
    public static int EnsureBrowserInstalled()
    {
        try
        {
            return Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
        }
        catch
        {
            return -1;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_browser is not null) await _browser.DisposeAsync(); } catch { }
        _playwright?.Dispose();
    }
}
