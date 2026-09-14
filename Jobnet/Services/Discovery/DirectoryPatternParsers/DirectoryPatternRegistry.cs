using System.Collections.Generic;
using System.Linq;

namespace Jobnet.Services.Discovery.DirectoryPatternParsers;

/// <summary>
/// Holds the set of hand-written <see cref="IDirectoryPatternParser"/> implementations and
/// resolves the right one for a given directory page. Mirrors <c>HtmlPatternRegistry</c> —
/// registration order = priority order, DI is the only entry point (see
/// <c>ServiceRegistration.AddJobnetCore</c>).
/// </summary>
public sealed class DirectoryPatternRegistry
{
    private readonly IReadOnlyList<IDirectoryPatternParser> _parsers;

    public DirectoryPatternRegistry(IEnumerable<IDirectoryPatternParser> parsers)
    {
        _parsers = parsers.ToList();
    }

    /// <summary>Friendly names of the registered parsers in priority order. Surfaced on the
    /// Parser Report screen.</summary>
    public IReadOnlyList<string> Names => _parsers.Select(p => p.Name).ToList();

    /// <summary>Return the first parser whose <c>CanHandle</c> says yes, or null when nothing
    /// matches (the caller falls through to AI extraction).</summary>
    public IDirectoryPatternParser? ResolveFor(string url, string html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        foreach (var p in _parsers)
        {
            try
            {
                if (p.CanHandle(url, html)) return p;
            }
            catch
            {
                // CanHandle should never throw, but if it does, don't poison the whole
                // pipeline — skip and let the next parser (or AI) have a chance.
            }
        }
        return null;
    }
}
