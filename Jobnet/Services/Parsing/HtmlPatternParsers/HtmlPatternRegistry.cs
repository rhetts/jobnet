using System;
using System.Collections.Generic;
using System.Linq;

namespace Jobnet.Services.Parsing.HtmlPatternParsers;

/// <summary>
/// Holds the set of hand-written <see cref="IHtmlPatternParser"/> implementations and resolves the
/// right one for a given page. Callers ask <see cref="ResolveFor"/> with the URL + rendered HTML
/// and either get a matching parser back or null (in which case the existing AI-extract path
/// kicks in instead).
///
/// Registration order = priority order. More-specific parsers (e.g. "this WP plugin") should
/// come before more-general ones (e.g. "any anchor list"). DI registration is the only public
/// entry: see <c>ServiceRegistration.AddJobnetCore</c>, where parsers are bound in priority
/// order and the registry constructor receives them via <c>IEnumerable&lt;IHtmlPatternParser&gt;</c>.
/// </summary>
public sealed class HtmlPatternRegistry
{
    private readonly IReadOnlyList<IHtmlPatternParser> _parsers;

    public HtmlPatternRegistry(IEnumerable<IHtmlPatternParser> parsers)
    {
        // Materialize once so resolution is allocation-free per call. DI gives us the
        // collection in registration order; we preserve that as priority.
        _parsers = parsers.ToList();
    }

    /// <summary>Friendly names of the registered parsers in priority order. Surfaced on the
    /// Parser Report screen so the user can see which patterns are wired in.</summary>
    public IReadOnlyList<string> Names => _parsers.Select(p => p.Name).ToList();

    /// <summary>Return the first parser whose <c>CanHandle</c> says yes, or null when nothing
    /// matches. Cheap — every <c>CanHandle</c> in this codebase is a substring check or two.</summary>
    public IHtmlPatternParser? ResolveFor(string url, string html)
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
                // A parser's CanHandle should never throw, but if it does we don't want to
                // poison the whole pipeline. Skip and let the next one have a chance.
            }
        }
        return null;
    }
}
