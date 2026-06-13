using System.Collections.Generic;
using Jobnet.Services.JobSources;

namespace Jobnet.Services.Parsing.HtmlPatternParsers;

/// <summary>
/// Hand-written extraction logic for a specific company (or a specific ATS pattern that several
/// companies share). Parsers are pure functions of (url, html) -> jobs: no network, no DB, no
/// AI. That makes them trivial to unit-test against captured HTML fixtures.
///
/// Routing: <see cref="HtmlPatternRegistry"/> asks each parser <c>CanHandle</c> in priority
/// order and uses the first one that says yes. A miss falls through to the existing AI-extract
/// path. Parsers don't need to be defensive — when in doubt, return CanHandle = false.
/// </summary>
public interface IHtmlPatternParser
{
    /// <summary>Stable identifier for logging / persistence. Used in run_step_log notes and the
    /// Parser Report screen so the user can see which hand-written parser ran.</summary>
    string Name { get; }

    /// <summary>True when this parser is the right one for the given page. Cheap to evaluate
    /// — typically a substring check on the URL or a known marker class in the HTML.</summary>
    bool CanHandle(string url, string html);

    /// <summary>Parse the page into structured job postings. <paramref name="baseUrl"/> is used
    /// to resolve relative <c>href</c> values to absolute URLs. May return an empty list when
    /// the page legitimately has no openings; throws on truly malformed input (corrupt HTML).</summary>
    IReadOnlyList<RawJobPosting> Parse(string html, string baseUrl);
}
