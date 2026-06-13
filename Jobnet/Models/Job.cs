using System;
using System.Collections.Generic;

namespace Jobnet.Models;

public sealed class Job
{
    public required int Id { get; init; }
    public required int CompanyId { get; init; }
    public required string Title { get; init; }
    public string? Url { get; init; }
    public string? Location { get; init; }
    public string? RemoteType { get; init; }
    public string? EmploymentType { get; init; }
    public int? LevelId { get; set; }
    public IReadOnlyList<int> AreaIds { get; set; } = Array.Empty<int>();
    public string? DescriptionSnippet { get; init; }
    public string? Summary { get; set; }
    public string? SalaryRange { get; init; }
    public int? SalaryMin { get; init; }
    public int? SalaryMax { get; init; }
    public string? SalaryCurrency { get; init; }
    public string? SalaryPeriod { get; init; }
    public int? ResumeMatchScore { get; set; }
    public string? ResumeMatchReason { get; set; }
    public InterestLevel InterestLevel { get; set; }
    public DateTime? DateApplied { get; set; }
    public DateTime? DateViewed { get; set; }
    public required DateTime DateFirstSeen { get; init; }
    public required DateTime DateLastSeen { get; init; }
    public DateTime? DateRemoved { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Which extraction path produced this job on the most recent upsert. One of the
    /// <c>AttemptStage</c>/source-stage strings: <c>ats_greenhouse</c>, <c>ats_lever</c>,
    /// <c>ai_extract</c>, <c>jsonld</c>, <c>hand_written:lever_shortcode</c>, etc. Persisted
    /// to <c>jobs.source_stage</c>. Drives <c>parser-stats --candidates</c>: AI-extract jobs
    /// that show up consistently are hand-written-parser candidates.</summary>
    public string? SourceStage { get; set; }
}
