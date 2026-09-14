using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Jobnet.Data;
using Jobnet.Services;
using Jobnet.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jobnet;

public partial class App : Application
{
    public IHost Host { get; }
    private string _logPath = string.Empty;

    public App()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var jobnetDir = Path.Combine(localAppData, "Jobnet");
        Directory.CreateDirectory(jobnetDir);
        _logPath = Path.Combine(jobnetDir, "jobnet.log");

        // All log writes use FileShare.ReadWrite | FileShare.Delete so a second Jobnet instance
        // (or a tail-following process) can read/append concurrently. The previous code held the
        // file open with the default FileShare.Read via TextWriterTraceListener; a leftover
        // zombie process would block every new launch from writing its startup banner.
        AppendShared(_logPath, $"\n=== App start {DateTime.Now:O} (pid {Environment.ProcessId}) ===\n");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogException("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, e) =>
        {
            LogException("Dispatcher.UnhandledException", e.Exception);
            e.Handled = false;
        };

        // Route WPF binding/trace errors to the same log so we can see them post-mortem. Wrap
        // an explicit FileStream so the share mode is ReadWrite|Delete — otherwise the listener
        // grabs an exclusive write lock and any other Jobnet instance dies on startup trying to
        // open the same file (see the zombie-process scenario we hit).
        var sharedStream = new FileStream(_logPath, FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        var sharedWriter = new StreamWriter(sharedStream) { AutoFlush = true };
        var listener = new TextWriterTraceListener(sharedWriter);
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

        Host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddDebug();
            })
            .ConfigureServices((_, services) =>
            {
                services.AddJobnetCore();

                services.AddSingleton<MainWindowViewModel>();
                services.AddSingleton<MainWindow>();

                services.AddTransient<SettingsViewModel>();
                services.AddTransient<Views.SettingsWindow>();
                services.AddSingleton<Func<Views.SettingsWindow>>(sp => () => sp.GetRequiredService<Views.SettingsWindow>());

                services.AddTransient<CompanyProfileViewModel>();
                services.AddTransient<Views.CompanyProfileWindow>();
                services.AddSingleton<Func<Views.CompanyProfileWindow>>(sp => () => sp.GetRequiredService<Views.CompanyProfileWindow>());

                // RefreshViewModel is a singleton so the in-flight refresh (StatusText, IsBusy,
                // last-run labels) survives closing/reopening the window. The Window itself stays
                // transient — a fresh WPF Window is created each time and rebinds to the same VM.
                services.AddSingleton<RefreshViewModel>();
                services.AddTransient<Views.RefreshWindow>();
                services.AddSingleton<Func<Views.RefreshWindow>>(sp => () => sp.GetRequiredService<Views.RefreshWindow>());

                services.AddTransient<SavedFiltersViewModel>();
                services.AddTransient<Views.SavedFiltersWindow>();
                services.AddSingleton<Func<Views.SavedFiltersWindow>>(sp => () => sp.GetRequiredService<Views.SavedFiltersWindow>());

                services.AddTransient<ResumeViewModel>();
                services.AddTransient<Views.ResumeWindow>();
                services.AddSingleton<Func<Views.ResumeWindow>>(sp => () => sp.GetRequiredService<Views.ResumeWindow>());

                services.AddTransient<ServiceLimitsViewModel>();
                services.AddTransient<Views.ServiceLimitsWindow>();
                services.AddSingleton<Func<Views.ServiceLimitsWindow>>(sp => () => sp.GetRequiredService<Views.ServiceLimitsWindow>());

                services.AddTransient<RunsViewModel>();
                services.AddTransient<Views.RunsWindow>();
                services.AddSingleton<Func<Views.RunsWindow>>(sp => () => sp.GetRequiredService<Views.RunsWindow>());

                services.AddTransient<StatsViewModel>();
                services.AddTransient<Views.StatsWindow>();
                services.AddSingleton<Func<Views.StatsWindow>>(sp => () => sp.GetRequiredService<Views.StatsWindow>());

                services.AddTransient<ParserReportViewModel>();
                services.AddTransient<Views.ParserReportWindow>();
                services.AddSingleton<Func<Views.ParserReportWindow>>(sp => () => sp.GetRequiredService<Views.ParserReportWindow>());

                services.AddTransient<CoverLetterViewModel>();
                services.AddTransient<Views.CoverLetterWindow>();
                services.AddSingleton<Func<Views.CoverLetterWindow>>(sp => () => sp.GetRequiredService<Views.CoverLetterWindow>());

                services.AddTransient<SourcesViewModel>();
                services.AddTransient<Views.SourcesWindow>();
                services.AddSingleton<Func<Views.SourcesWindow>>(sp => () => sp.GetRequiredService<Views.SourcesWindow>());

                services.AddTransient<ScanTimesViewModel>();
                services.AddTransient<Views.ScanTimesWindow>();
                services.AddSingleton<Func<Views.ScanTimesWindow>>(sp => () => sp.GetRequiredService<Views.ScanTimesWindow>());

                services.AddTransient<FiltersViewModel>();
                services.AddTransient<Views.FiltersWindow>();
                services.AddSingleton<Func<Views.FiltersWindow>>(sp => () => sp.GetRequiredService<Views.FiltersWindow>());
            })
            .Build();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            Host.Start();
            Host.Services.GetRequiredService<MigrationRunner>().Run();

            // Clean up any run_log rows left as 'running' from a previous crash or kill, and
            // backfill their aggregate counts from completed step rows so the history page
            // shows what actually happened.
            try
            {
                var cleaned = Host.Services.GetRequiredService<Services.Logging.IRunLogger>().CleanupDanglingRuns();
                if (cleaned > 0)
                    AppendShared(_logPath, $"Run-log cleanup: marked {cleaned} dangling row(s) as 'interrupted'.\n");
            }
            catch (Exception cleanupEx)
            {
                LogException("RunLogCleanup", cleanupEx);
            }

            // Same idea for the worker queue: rows stuck in 'running' from a prior process
            // session need to be reset to 'pending' so this session's workers can re-claim them.
            try
            {
                var reset = Host.Services.GetRequiredService<Data.Repositories.IJobProcessingQueueRepository>()
                    .ResetStaleRunning();
                if (reset > 0)
                    AppendShared(_logPath, $"Queue cleanup: reset {reset} stale 'running' row(s) to 'pending'.\n");
            }
            catch (Exception queueEx)
            {
                LogException("QueueCleanup", queueEx);
            }

            // One-off backfill: derive size_category for any company that has a profile_size_hint
            // but no size_category yet. Cheap (one UPDATE per existing profile), idempotent, and
            // a no-op once everyone's been classified.
            try
            {
                var classifier = (Func<string?, string?>)(hint => Services.Profiling.CompanySizeClassifier.Classify(hint));
                var sizeUpdated = Host.Services.GetRequiredService<Data.Repositories.ICompanyRepository>()
                    .BackfillSizeCategories(classifier);
                if (sizeUpdated > 0)
                    AppendShared(_logPath, $"Size backfill: classified {sizeUpdated} company size hint(s).\n");
            }
            catch (Exception sizeEx)
            {
                LogException("SizeBackfill", sizeEx);
            }

            var window = Host.Services.GetRequiredService<MainWindow>();
            window.Show();
            AppendShared(_logPath, "Main window shown OK\n");

            // Start the queue workers AFTER the main window is showing so any startup error
            // surfaces in the foreground first. Workers run for the lifetime of the app and
            // are torn down in OnExit.
            try
            {
                Host.Services.GetRequiredService<Services.Workers.WorkerHost>().Start();
                AppendShared(_logPath, "Queue workers started\n");
            }
            catch (Exception workerEx) { LogException("WorkerHost.Start", workerEx); }
        }
        catch (Exception ex)
        {
            LogException("OnStartup", ex);
            MessageBox.Show(
                $"Startup failed: {ex.GetType().Name}\n\n{ex.Message}\n\nSee {_logPath}",
                "Jobnet — startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Append a line to <paramref name="path"/> using <see cref="FileShare.ReadWrite"/> +
    /// <see cref="FileShare.Delete"/> so a second process holding the same file open in any mode
    /// doesn't block us. Swallows IO errors — logging must never crash the host.</summary>
    private static void AppendShared(string path, string text)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            using var sw = new StreamWriter(fs);
            sw.Write(text);
        }
        catch { /* logging is best-effort */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Stop the queue workers first — they may be mid-AI-call. The host gives them up to 5s
        // to drain, then signals cancellation and moves on. This must come before Host.StopAsync
        // because the workers depend on services owned by the host.
        //
        // Run on a thread-pool thread rather than awaiting directly on this (UI) thread: OnExit
        // runs on the dispatcher thread, which has a DispatcherSynchronizationContext current.
        // WorkerHost.StopAsync()'s own `await` (and everything it in turn awaits, several layers
        // deep into the AI clients) captures that context by default and tries to resume back on
        // it — but this thread is sitting right here blocked on the result, so that continuation
        // can never run. StopAsync()'s internal 5s bound never gets a chance to matter because its
        // *own task never completes* — this is what actually kept Jobnet.exe alive indefinitely
        // after the main window closed, not any single slow operation. Task.Run has no captured
        // context, so everything downstream resumes on the thread pool instead of deadlocking here.
        try
        {
            Task.Run(() => Host.Services.GetService<Services.Workers.WorkerHost>()?.StopAsync() ?? Task.CompletedTask)
                .Wait(TimeSpan.FromSeconds(7));
        }
        catch (Exception ex) { LogException("OnExit.WorkerHostStop", ex); }

        // Explicitly dispose resources that own native handles / child processes — Host.Dispose
        // alone doesn't reach them deterministically, so without this Jobnet.exe stays alive
        // after the main window closes (Playwright keeps its Chromium worker; LLamaSharp keeps
        // GPU memory mapped).
        try
        {
            if (Host.Services.GetService<Services.Playwright.IPlaywrightFetcher>() is IAsyncDisposable pw)
                pw.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex) { LogException("OnExit.PlaywrightDispose", ex); }

        try
        {
            (Host.Services.GetService<Services.Ai.LLamaClient>() as IDisposable)?.Dispose();
        }
        catch (Exception ex) { LogException("OnExit.LLamaDispose", ex); }

        // Same sync-over-async deadlock risk as the WorkerHost stop above — escape the captured
        // dispatcher context via Task.Run rather than awaiting directly on this thread. (An
        // earlier fix here wrapped this same GetAwaiter().GetResult() call in try/catch instead,
        // on the theory that an uncaught exception was corrupting shutdown — that doesn't hold:
        // a deadlock never throws, so a try/catch around it never fires and the call still hangs
        // forever. Verified empirically that this Task.Run version actually exits: 4 close trials,
        // all 0.1-4.3s, vs. confirmed-hung past 120s before.)
        try { Task.Run(() => Host.StopAsync(TimeSpan.FromSeconds(2))).Wait(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { LogException("OnExit.HostStopAsync", ex); }

        // Host.Dispose() re-disposes every singleton the container ever resolved — including the
        // two already disposed explicitly above. Their Dispose methods are idempotent (see
        // LLamaClient.Dispose()'s _disposed guard), so this is normally a harmless no-op, but
        // catch anyway rather than let an unrelated disposal bug take the rest of shutdown down.
        try
        {
            Host.Dispose();
        }
        catch (Exception ex) { LogException("OnExit.HostDispose", ex); }

        base.OnExit(e);
    }

    private void LogException(string source, Exception? ex)
    {
        if (ex is null) return;
        AppendShared(_logPath,
            $"[{DateTime.Now:O}] [{source}] {ex.GetType().FullName}: {ex.Message}\n{ex}\n\n");
    }
}
