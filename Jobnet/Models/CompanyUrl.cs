using System;

namespace Jobnet.Models;

public sealed class CompanyUrl
{
    public int Id { get; init; }
    public required int CompanyId { get; init; }
    public required string Url { get; init; }
    public required string Kind { get; init; }
    public string? Label { get; init; }
    public string? DiscoveredVia { get; init; }
    public int FailCount { get; set; }
    public DateTime LastSeen { get; set; }
    public DateTime? LastYielded { get; set; }
}

public static class UrlKind
{
    public const string CareersRoot = "careers_root";
    public const string Department  = "department";
    public const string JobList     = "job_list";
    public const string AtsApi      = "ats_api";
    public const string JobDetail   = "job_detail";
    public const string Unknown     = "unknown";
}
