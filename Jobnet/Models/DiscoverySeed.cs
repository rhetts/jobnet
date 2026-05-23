using System;

namespace Jobnet.Models;

public sealed class DiscoverySeed
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string Url { get; init; }
    public string? Description { get; init; }
    public bool IsEnabled { get; init; } = true;
    public int SortOrder { get; init; }
    public int MaxPages { get; init; } = 1;
    public DateTime DateAdded { get; init; }
}
