using System.Collections.Generic;

namespace Jobnet.ViewModels;

/// <summary>JSON-serializable snapshot of every filter control on the main window.</summary>
public sealed class FilterStateSnapshot
{
    public string Keyword { get; set; } = "";
    public List<int> LevelIds { get; set; } = new();
    public List<int> AreaIds  { get; set; } = new();
    public List<string> CityNames { get; set; } = new();
    public bool ShowAllCompanies { get; set; }
    public bool ShowRemovedJobs  { get; set; }
}
