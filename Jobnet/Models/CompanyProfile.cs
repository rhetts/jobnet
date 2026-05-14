using System;
using System.Collections.Generic;

namespace Jobnet.Models;

public sealed class CompanyProfile
{
    public string? Summary { get; init; }
    public IReadOnlyList<string> Products { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Industries { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> TechSignals { get; init; } = Array.Empty<string>();
    public string? HeadquartersHint { get; init; }
    public string? SizeHint { get; init; }
    public DateTime? GeneratedAt { get; init; }
    public string? Model { get; init; }
}
