namespace Jobnet.Services.Logging;

/// <summary>Stage identifiers for <c>refresh_attempt.stage</c>. Strings, not an enum, because
/// the DB stores them as text and we want the constants to match exactly. Adding a new stage:
/// add a constant here, then write the row from wherever the stage runs.</summary>
public static class AttemptStage
{
    public const string DetectAts        = "detect_ats";
    public const string AtsApi           = "ats_api";
    public const string CachedUrl        = "cached_url";
    public const string PlaywrightFetch  = "playwright_fetch";
    public const string JsonLd           = "jsonld";
    public const string HandWritten      = "hand_written";
    public const string CachedSelectors  = "selectors";
    public const string AiExtract        = "ai_extract";
    public const string DomHashSkip      = "dom_hash_skip";
}

/// <summary>Result identifiers for <c>refresh_attempt.result</c>. Coarse enough that a single
/// dashboard can group by them; fine enough that "fetch_4xx" and "all_jobs_location_filtered"
/// are distinguishable.</summary>
public static class AttemptResult
{
    public const string Success                  = "success";
    public const string Empty                    = "empty";              // adapter returned 0 jobs cleanly
    public const string Http4xx                  = "http_4xx";
    public const string Http5xx                  = "http_5xx";
    public const string Timeout                  = "timeout";
    public const string ParseException           = "parse_exception";
    public const string HallucinationRejected    = "hallucination_rejected";
    public const string FilteredOut              = "filtered_out";       // jobs returned but all dropped by location/dept filter
    public const string Skipped                  = "skipped";            // skipped intentionally (e.g. dom unchanged)
    public const string CacheHit                 = "cache_hit";
}

/// <summary>Outcome-kind identifiers for <c>run_step_log.outcome_kind</c>. Same purpose as
/// AttemptResult but at the step (company) level — captures the *final* verdict per company
/// even when multiple stages ran.</summary>
public static class OutcomeKind
{
    public const string Success                  = "success";
    public const string Fetch4xx                 = "fetch_4xx";
    public const string Fetch5xx                 = "fetch_5xx";
    public const string FetchTimeout             = "fetch_timeout";
    public const string FetchBlocked             = "fetch_blocked";
    public const string ApiReturnedEmpty         = "api_returned_empty";
    public const string AllJobsLocationFiltered  = "all_jobs_location_filtered";
    public const string AllJobsDepartmentFiltered= "all_jobs_department_filtered";
    public const string ParseException           = "parse_exception";
    public const string AiHallucinationRejected  = "ai_hallucination_rejected";
    public const string CancelledUser            = "cancelled_user";
    public const string DomUnchangedSkip         = "dom_unchanged_skip";
    public const string NoAdapter                = "no_adapter";
}
