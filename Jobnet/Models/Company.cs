using System;

namespace Jobnet.Models;

public sealed class Company
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string Domain { get; init; }
    public string? WebsiteUrl { get; init; }
    public string? CareersUrl { get; init; }
    public string? City { get; init; }
    public string? AtsType { get; init; }
    public string? AtsSlug { get; init; }
    public string? AtsDepartmentFilter { get; init; }
    public string? Notes { get; init; }
    public InterestLevel InterestLevel { get; set; }
    public DateTime DateDiscovered { get; init; }
    public DateTime? DateLastScan { get; init; }

    /// <summary>True when this company is a recruitment agency, not a direct employer.
    /// Surfaced as a UI badge so the user can tell who they'd actually be applying to,
    /// and supports an optional filter to hide agency postings entirely.</summary>
    public bool IsAgency { get; init; }

    /// <summary>AI-derived selector-profile JSON used by the deterministic SelectorParser.
    /// Null until the first refresh runs through AiSelectorDeriver. Replayed in place of
    /// a full AI call on every subsequent refresh.</summary>
    public string? ParserStrategy { get; set; }

    /// <summary>When true the selector-replayer path is skipped for this company and refreshes
    /// always go through AI extraction. Set from the Parser Report screen for problem boards.</summary>
    public bool ParserStrategyDisabled { get; set; }

    /// <summary>UTC timestamp the current ParserStrategy was derived. Null when no profile cached.</summary>
    public DateTime? ParserStrategyDerivedAt { get; set; }

    /// <summary>One of: "ok", "drift", "error", or null. Last outcome of running the selector
    /// profile against a refresh. "drift" = 0 jobs returned when the company previously had some,
    /// triggering an AI re-derive.</summary>
    public string? ParserStrategyLastResult { get; set; }

    public DateTime? ParserStrategyLastResultAt { get; set; }

    /// <summary>Short error message from the last selector-eval failure, if any. Cleared on success.</summary>
    public string? ParserStrategyLastError { get; set; }
}
