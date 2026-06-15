using System.Threading;
using System.Threading.Tasks;

namespace Jobnet.Services.Workers;

/// <summary>
/// Owns the lifecycle of the background queue workers. WPF doesn't have hosted services out
/// of the box — the App calls <see cref="Start"/> after the DI container is built and
/// <see cref="StopAsync"/> on shutdown. Each worker runs as its own long-lived Task tied to
/// the shared cancellation token.
/// </summary>
public sealed class WorkerHost
{
    private readonly SummaryWorker _summary;
    private readonly ResumeMatchWorker _resumeMatch;
    private readonly CompanyProfileWorker _companyProfile;
    private readonly CancellationTokenSource _cts = new();
    private Task? _summaryTask;
    private Task? _resumeMatchTask;
    private Task? _companyProfileTask;
    private bool _started;

    public WorkerHost(SummaryWorker summary, ResumeMatchWorker resumeMatch, CompanyProfileWorker companyProfile)
    {
        _summary = summary;
        _resumeMatch = resumeMatch;
        _companyProfile = companyProfile;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        // Detached tasks; the host keeps the references so StopAsync can wait on them.
        _summaryTask        = Task.Run(() => _summary.RunAsync(_cts.Token));
        _resumeMatchTask    = Task.Run(() => _resumeMatch.RunAsync(_cts.Token));
        _companyProfileTask = Task.Run(() => _companyProfile.RunAsync(_cts.Token));
    }

    /// <summary>Signal cancellation and wait briefly for all workers to drain. Called on app
    /// shutdown — a 5s ceiling means closing the app never hangs more than that even if a
    /// worker is mid-AI-call.</summary>
    public async Task StopAsync()
    {
        if (!_started) return;
        try { _cts.Cancel(); } catch { }
        try
        {
            var all = Task.WhenAll(_summaryTask ?? Task.CompletedTask,
                                    _resumeMatchTask ?? Task.CompletedTask,
                                    _companyProfileTask ?? Task.CompletedTask);
            await Task.WhenAny(all, Task.Delay(5000));
        }
        catch { /* worker exceptions on cancellation are swallowed — they're not actionable */ }
    }
}
