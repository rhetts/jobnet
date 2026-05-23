using System;

namespace Jobnet.Models;

public sealed class SavedFilter
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string Payload { get; init; }     // JSON: FilterStateSnapshot
    public required DateTime DateCreated { get; init; }
    public DateTime? DateUsed { get; init; }
}
