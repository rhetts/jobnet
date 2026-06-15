using System;

namespace Jobnet.Services.Workers;

/// <summary>
/// Pub/sub bridge between background workers and the UI. Workers fire
/// <see cref="EntityChanged"/> after a successful task completion; the WPF
/// ViewModel subscribes and refreshes the affected row on its dispatcher.
///
/// Events are raised on the worker thread — subscribers are responsible for
/// marshalling to the UI thread. Keeping the notifier UI-agnostic means the
/// CLI / tests can use it too without dragging WPF in.
/// </summary>
public interface IEntityChangeNotifier
{
    event EventHandler<EntityChangedEventArgs>? EntityChanged;

    /// <summary>Raise an EntityChanged event. Workers call this after the queue row
    /// is marked completed so listeners can refresh the affected row. Swallows any
    /// listener exceptions — pub/sub must never break the worker's drain loop.</summary>
    void Notify(string entityType, int entityId, string field);
}

public sealed class EntityChangedEventArgs : EventArgs
{
    /// <summary>"job" | "company" — interpretation key for <see cref="EntityId"/>.</summary>
    public required string EntityType { get; init; }
    public required int EntityId { get; init; }
    /// <summary>Which logical field changed — "summary", "resume_match", "profile".
    /// Listeners can filter cheaply if they only care about a subset.</summary>
    public required string Field { get; init; }
}

public sealed class EntityChangeNotifier : IEntityChangeNotifier
{
    public event EventHandler<EntityChangedEventArgs>? EntityChanged;

    public void Notify(string entityType, int entityId, string field)
    {
        var handler = EntityChanged;
        if (handler is null) return;
        try
        {
            handler(this, new EntityChangedEventArgs
            {
                EntityType = entityType,
                EntityId = entityId,
                Field = field,
            });
        }
        catch (Exception ex)
        {
            // A throwing subscriber must NOT poison the worker. Log and move on.
            System.Diagnostics.Debug.WriteLine($"[entity-change-notifier] handler threw: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

public static class EntityChangeKinds
{
    public const string Job        = "job";
    public const string Company    = "company";

    public const string Summary    = "summary";
    public const string ResumeMatch = "resume_match";
    public const string Profile    = "profile";
}
