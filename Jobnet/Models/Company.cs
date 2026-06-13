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

    /// <summary>When false, the company is skipped by RefreshAllAsync. Used to retire
    /// acquired / defunct / wrong-domain entries without losing their historical jobs.
    /// Defaults to true on insert (see migration 001 default).</summary>
    public bool IsActive { get; init; } = true;

    /// <summary>True when this company is a recruitment agency, not a direct employer.
    /// Surfaced as a UI badge so the user can tell who they'd actually be applying to,
    /// and supports an optional filter to hide agency postings entirely.</summary>
    public bool IsAgency { get; init; }

    /// <summary>AI-derived selector-profile JSON used by the deterministic SelectorProfileReplayer.
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

    /// <summary>Name of the hand-written <c>IHtmlPatternParser</c> (e.g. "lever_shortcode") that
    /// produced jobs for this company on its most recent refresh. Null when the company has
    /// never matched a hand-written parser. Shown on the Parser Report so the user can see
    /// which pattern is actually serving which company.</summary>
    public string? LastCompanyParser { get; set; }

    /// <summary>Count of refresh-jobs runs in a row that yielded zero jobs (or failed). Resets
    /// to 0 on any refresh that yields ≥1 job. Drives the auto-clear-stale-slug rule: when this
    /// hits 2 *and* the last refresh produced a 4xx, JobRefresher clears <see cref="AtsSlug"/>
    /// so the next refresh re-runs detect-ats.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>UTC timestamp of the last refresh that yielded ≥1 job for this company. Null
    /// for companies that have never produced a job. Used together with
    /// <see cref="DateLastScan"/> to distinguish "never refreshed" from "refreshed but always
    /// empty".</summary>
    public DateTime? LastSuccessAt { get; set; }

    /// <summary>Job count from the most recent refresh (regardless of new/updated/total). Drives
    /// 0-yield drift detection — when this was &gt;0 but the current refresh returned 0, the
    /// slug/page has probably changed shape and we should re-detect.</summary>
    public int? LastRefreshJobsCount { get; set; }
}
