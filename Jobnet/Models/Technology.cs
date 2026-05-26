namespace Jobnet.Models;

public sealed class Technology
{
    public required int Id { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
}
