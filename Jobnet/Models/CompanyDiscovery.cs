using System;

namespace Jobnet.Models;

public sealed class CompanyDiscovery
{
    public required int Id { get; init; }
    public required int CompanyId { get; init; }
    public required string SourceType { get; init; }
    public required string SourceName { get; init; }
    public string? SourceUrl { get; init; }
    public long? RunId { get; init; }
    public required DateTime DiscoveredAt { get; init; }
}
