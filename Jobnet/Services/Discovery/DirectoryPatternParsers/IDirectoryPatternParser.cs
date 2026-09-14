using System.Collections.Generic;

namespace Jobnet.Services.Discovery.DirectoryPatternParsers;

/// <summary>
/// Hand-written, deterministic extraction logic for a specific directory/portfolio page —
/// the directory-harvest analogue of <c>IHtmlPatternParser</c> for company career pages.
/// Directories are a better fit for hand-written selectors than individual companies: a given
/// directory's template rarely changes, whereas career pages vary wildly company to company
/// (which is why companies go through AI/cached-selectors instead of hand-written parsers).
///
/// Parsers are pure functions of (url, html) -> candidates: no network, no DB, no AI. Routing:
/// <see cref="DirectoryPatternRegistry"/> asks each parser <c>CanHandle</c> in registration
/// order and uses the first one that says yes. A miss (CanHandle = false) falls through to the
/// AI-extract path, same as the miss case for company parsers.
///
/// Unlike <c>IHtmlPatternParser</c>, a genuine parse failure here (the page's template changed
/// enough that the parser throws) must NOT be swallowed — <see cref="CompanyDirectoryHarvester"/>
/// catches the exception, logs it clearly, records it on the owning <c>DiscoverySeed</c> row so
/// it shows up on the Parser Report screen, and falls back to AI for that crawl so the harvest
/// doesn't just silently produce nothing.
/// </summary>
public interface IDirectoryPatternParser
{
    /// <summary>Stable identifier for logging / persistence — shown in <c>discovery_seeds
    /// .custom_parser_name</c> and on the Parser Report screen.</summary>
    string Name { get; }

    /// <summary>True when this parser is the right one for the given page. Cheap to evaluate —
    /// typically a host check on the URL, optionally combined with a marker class/id in the
    /// HTML to guard against the site changing platforms entirely.</summary>
    bool CanHandle(string url, string html);

    /// <summary>Parse the page into company candidates. <paramref name="baseUrl"/> resolves
    /// relative <c>href</c> values to absolute URLs. Return an empty list when the page
    /// legitimately has no entries (not an error); throw when the expected structure isn't
    /// found at all (template changed) — that's the signal the harvester logs and surfaces.</summary>
    IReadOnlyList<DirectoryCandidate> Parse(string html, string baseUrl);
}

/// <summary>One company found on a directory page. Mirrors the shape
/// <c>CompanyDirectoryHarvester</c> already builds from AI output, so both paths feed the same
/// downstream dedup/filter/insert logic.</summary>
public sealed class DirectoryCandidate
{
    public required string Name { get; init; }
    public string? Url { get; init; }
    public string? City { get; init; }
}
