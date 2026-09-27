using System.Collections.Generic;

namespace Jobnet.Services.JobSources;

/// <summary>Normalized job posting as returned by an ATS adapter. All fields optional except Title + NativeId.</summary>
public sealed class RawJobPosting
{
    /// <summary>The ATS's stable identifier for this posting.</summary>
    public required string NativeId { get; init; }
    public required string Title { get; init; }
    public string? Url { get; init; }
    public string? Location { get; init; }
    /// <summary>Additional locations a posting is open to (e.g. Ashby's secondaryLocations).
    /// The Vancouver-area gate accepts a posting if Location OR any of these match.</summary>
    public IReadOnlyList<string>? SecondaryLocations { get; init; }
    public string? RemoteType { get; init; }         // 'on-site' | 'hybrid' | 'remote' | 'unknown'
    public string? EmploymentType { get; init; }
    public string? Department { get; init; }
    public string? DescriptionSnippet { get; init; }
    public string? SalaryRange { get; init; }
    public int? SalaryMin { get; init; }
    public int? SalaryMax { get; init; }
    public string? SalaryCurrency { get; init; }
    public string? SalaryPeriod { get; init; }   // 'year' | 'month' | 'hour' | null
}
