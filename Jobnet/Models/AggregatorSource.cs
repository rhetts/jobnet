namespace Jobnet.Models;

public sealed class AggregatorSource
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string BaseUrl { get; init; }
    public string? SearchUrlTemplate { get; init; }
    public bool IsEnabled { get; set; }
    public string? Notes { get; init; }
}
