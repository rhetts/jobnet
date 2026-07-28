using System;
using Jobnet.Data.Repositories;

namespace Jobnet.Services.Filters;

/// <summary>
/// Hands out a compiled <see cref="FilterRuleSet"/>, rebuilding it at most once every
/// <see cref="TtlSeconds"/>. Callers deep in the fetch path (UrlClassifier via
/// AiFallbackJobSource) can't easily thread a per-batch instance down to themselves, and
/// recompiling ~100 regexes per URL would be wasteful.
///
/// The TTL means an edit in Settings takes effect within a minute even mid-scan;
/// <see cref="Invalidate"/> makes it immediate when the UI saves.
/// </summary>
public sealed class FilterRuleProvider
{
    private const int TtlSeconds = 60;

    private readonly IFilterRuleRepository _repo;
    private readonly object _lock = new();
    private FilterRuleSet? _cached;
    private DateTime _loadedAtUtc;

    public FilterRuleProvider(IFilterRuleRepository repo)
    {
        _repo = repo;
    }

    public FilterRuleSet Current
    {
        get
        {
            lock (_lock)
            {
                if (_cached is null || (DateTime.UtcNow - _loadedAtUtc).TotalSeconds > TtlSeconds)
                {
                    // A failed load must not take the scan down — fall back to whatever we had,
                    // or to an empty set (which filters nothing) on first load.
                    try
                    {
                        _cached = FilterRuleSet.Load(_repo);
                        _loadedAtUtc = DateTime.UtcNow;
                    }
                    catch
                    {
                        _cached ??= FilterRuleSet.Empty();
                    }
                }
                return _cached;
            }
        }
    }

    /// <summary>Force a reload on the next access. Call after the user edits rules.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _cached?.FlushHits();
            _cached = null;
        }
    }

    /// <summary>Persist accumulated match counts. Call at the end of a run.</summary>
    public void FlushHits()
    {
        lock (_lock) { _cached?.FlushHits(); }
    }
}
