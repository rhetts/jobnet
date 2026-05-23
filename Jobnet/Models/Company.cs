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
}
