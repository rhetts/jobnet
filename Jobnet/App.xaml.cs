using System;
using System.Diagnostics;
using System.IO;
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

        File.AppendAllText(_logPath, $"\n=== App start {DateTime.Now:O} ===\n");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogException("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, e) =>
        {
            LogException("Dispatcher.UnhandledException", e.Exception);
            e.Handled = false;
        };

        // Route WPF binding/trace errors to the same log so we can see them post-mortem
        var listener = new TextWriterTraceListener(_logPath);
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
                    File.AppendAllText(_logPath, $"Run-log cleanup: marked {cleaned} dangling row(s) as 'interrupted'.\n");
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
                    File.AppendAllText(_logPath, $"Queue cleanup: reset {reset} stale 'running' row(s) to 'pending'.\n");
            }
            catch (Exception queueEx)
            {
                LogException("QueueCleanup", queueEx);
            }

            var window = Host.Services.GetRequiredService<MainWindow>();
            window.Show();
            File.AppendAllText(_logPath, "Main window shown OK\n");

            // Start the queue workers AFTER the main window is showing so any startup error
            // surfaces in the foreground first. Workers run for the lifetime of the app and
            // are torn down in OnExit.
            try
            {
                Host.Services.GetRequiredService<Services.Workers.WorkerHost>().Start();
                File.AppendAllText(_logPath, "Queue workers started\n");
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

    protected override void OnExit(ExitEventArgs e)
    {
        // Stop the queue workers first — they may be mid-AI-call. The host gives them up to 5s
        // to drain, then signals cancellation and moves on. This must come before Host.StopAsync
        // because the workers depend on services owned by the host.
        try
        {
            Host.Services.GetService<Services.Workers.WorkerHost>()?.StopAsync()
                .GetAwaiter().GetResult();
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

        Host.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        Host.Dispose();
        base.OnExit(e);
    }

    private void LogException(string source, Exception? ex)
    {
        if (ex is null) return;
        try
        {
            File.AppendAllText(_logPath,
                $"[{DateTime.Now:O}] [{source}] {ex.GetType().FullName}: {ex.Message}\n{ex}\n\n");
        }
        catch { /* swallow logging failures */ }
    }
}
