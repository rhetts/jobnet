namespace Jobnet.Models;

public sealed class Level
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public int SortOrder { get; set; }
}
