using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
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

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private bool _disposed;

    public PlaywrightFetcher(IApiUsageTracker usage, IRateLimiter rateLimiter)
    {
        _usage = usage;
        _rateLimiter = rateLimiter;
    }

    public async Task<PlaywrightFetchResult> FetchAsync(string url, CancellationToken ct = default)
    {
        await _rateLimiter.WaitAsync(Provider, ct);
        _usage.RecordCall(Provider);

        var browser = await EnsureBrowserAsync(ct);
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/Jobnet/0.7 Safari/537.36",
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
        });
        try
        {
            var page = await context.NewPageAsync();
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
                        Error = "Page did not reach network-idle within 30s; returned partial render"
                    };
                }
                catch
                {
                    return new PlaywrightFetchResult { FinalUrl = url, HttpStatus = 0, Html = "", Success = false, Error = "Timeout, no content" };
                }
            }
            catch (Exception ex)
            {
                return new PlaywrightFetchResult { FinalUrl = url, HttpStatus = 0, Html = "", Success = false, Error = ex.Message };
            }
        }
        finally
        {
            await context.CloseAsync();
        }
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
