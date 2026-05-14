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
            var window = Host.Services.GetRequiredService<MainWindow>();
            window.Show();
            File.AppendAllText(_logPath, "Main window shown OK\n");
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
