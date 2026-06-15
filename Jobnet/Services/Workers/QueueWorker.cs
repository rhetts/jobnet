using System;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Services.Workers;

/// <summary>
/// Long-running poll loop for one task type. Each worker subclass dequeues N items per
/// cycle, processes them, marks each row complete or failed in the queue. Tied to the
/// app's lifetime via a CancellationToken from <see cref="WorkerHost"/>.
///
/// Design choices worth knowing:
/// * One <c>IServiceScope</c> per cycle, not per item. The scope owns Dapper connections;
///   keeping it short stops any single bad poll from holding a DB lock for hours. Per-item
///   scoping would be even safer but multiplies overhead — five rows × the scope cost.
/// * Errors inside <see cref="ProcessOneAsync"/> are swallowed and recorded on the queue
///   row, NOT rethrown. We don't want one bad job to kill the worker. Catastrophic errors
///   (DB unreachable, AI provider misconfigured globally) get logged + sleep + retry.
/// * Each subclass owns its config keys (<c>worker.{name}.enabled</c>,
///   <c>worker.{name}.poll_seconds</c>, <c>worker.{name}.batch_size</c>) so they can be
///   tuned / disabled independently from Settings.
/// </summary>
public abstract class QueueWorker
{
    private readonly IServiceScopeFactory _scopes;
    protected abstract string WorkerName { get; }
    protected abstract string TaskType { get; }
    /// <summary>Default cycle interval when the worker has no work. When a cycle DID dequeue
    /// items, the next iteration runs immediately to drain the backlog quickly.</summary>
    protected virtual int DefaultPollSeconds => 30;
    protected virtual int DefaultBatchSize => 5;

    protected QueueWorker(IServiceScopeFactory scopes) { _scopes = scopes; }

    /// <summary>Process one already-dequeued item. Implementations should NOT touch the queue
    /// row themselves — return true on success, false (or throw) on failure, and the loop
    /// marks the row appropriately.</summary>
    protected abstract Task<bool> ProcessOneAsync(IServiceProvider scoped, DequeuedItem item, CancellationToken ct);

    /// <summary>Override to publish an <see cref="IEntityChangeNotifier"/> event after a
    /// successful row. Default no-op; subclasses fire when they want the UI to refresh
    /// (e.g. summary → reload that job's row). Notification fires AFTER the queue row is
    /// marked completed so listeners see consistent state.</summary>
    protected virtual void NotifyAfterSuccess(IServiceProvider scoped, DequeuedItem item) { }

    /// <summary>Optional pre-flight check — return false to skip this cycle entirely (e.g. AI
    /// provider not configured, resume not uploaded). Default: always run.</summary>
    protected virtual bool ShouldRunCycle(IServiceProvider scoped) => true;

    public async Task RunAsync(CancellationToken ct)
    {
        // Initial sleep — let the app finish startup before the worker starts hitting the DB.
        try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            var cycleHadWork = false;
            try
            {
                using var scope = _scopes.CreateScope();
                var sp = scope.ServiceProvider;
                var config = sp.GetRequiredService<IConfigRepository>();

                var enabled = string.Equals(
                    config.GetOrDefault($"worker.{WorkerName}.enabled", "true"),
                    "true", StringComparison.OrdinalIgnoreCase);

                if (enabled && ShouldRunCycle(sp))
                {
                    var batchSize = ReadInt(config, $"worker.{WorkerName}.batch_size", DefaultBatchSize);
                    var maxAttempts = ReadInt(config, "worker.max_attempts", 3);
                    var queue = sp.GetRequiredService<IJobProcessingQueueRepository>();
                    var batch = queue.ClaimNext(TaskType, batchSize);
                    cycleHadWork = batch.Count > 0;
                    foreach (var item in batch)
                    {
                        if (ct.IsCancellationRequested) break;
                        try
                        {
                            var ok = await ProcessOneAsync(sp, item, ct);
                            if (ok)
                            {
                                queue.MarkCompleted(item.QueueId);
                                // Best-effort UI notification — fire after the row is marked
                                // so any listener that re-reads the DB sees the completed state.
                                try { NotifyAfterSuccess(sp, item); } catch { }
                            }
                            else queue.MarkFailed(item.QueueId, "Processing returned false.", maxAttempts);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            queue.MarkFailed(item.QueueId,
                                $"{ex.GetType().Name}: {ex.Message}", maxAttempts);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Cycle-level catastrophe (DB unreachable, etc). Don't kill the worker —
                // sleep and try again. Surface the failure via Debug.WriteLine; the worker
                // can't write to its own queue if the queue itself is unreachable.
                System.Diagnostics.Debug.WriteLine($"[{WorkerName}-worker] cycle failed: {ex.GetType().Name}: {ex.Message}");
            }

            // Idle sleep = configured. Backlog drain = immediate next cycle, but with a small
            // courtesy delay so a 1000-row backlog doesn't peg the DB.
            var pollSeconds = ReadPollSeconds();
            var delay = cycleHadWork ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(pollSeconds);
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private int ReadPollSeconds()
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
            return ReadInt(config, $"worker.{WorkerName}.poll_seconds", DefaultPollSeconds);
        }
        catch { return DefaultPollSeconds; }
    }

    private static int ReadInt(IConfigRepository config, string key, int fallback)
    {
        var raw = config.GetOrDefault(key, "");
        return int.TryParse(raw, out var n) && n > 0 ? n : fallback;
    }
}
