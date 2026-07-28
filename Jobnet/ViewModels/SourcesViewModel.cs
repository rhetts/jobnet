using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jobnet.Data.Repositories;
using Jobnet.Services.Profiling;

namespace Jobnet.ViewModels;

/// <summary>One row in the Sources list — a company, a discovery seed, or an aggregator board.
/// Kind drives the chip + the type-filter dropdown; SizeCategory is only set for companies.</summary>
public sealed partial class SourceRow : ObservableObject
{
    public required string Kind { get; init; }       // "company" | "directory" | "board"
    public required string KindLabel { get; init; }  // "Company" | "Directory" | "Board"
    public required string Name { get; init; }
    public string? Url { get; init; }                // The link the user clicks
    public string? Secondary { get; init; }          // e.g. "Vancouver · Sequoia portfolio" / "5 pages"
    public string? SizeCategory { get; init; }       // null for non-company rows
    public string SizeLabel => CompanySizeCategories.Label(SizeCategory);
    public bool IsActive { get; init; } = true;

    [RelayCommand]
    private void OpenLink()
    {
        if (string.IsNullOrWhiteSpace(Url)) return;
        try { Process.Start(new ProcessStartInfo { FileName = Url, UseShellExecute = true }); }
        catch { /* user gets no feedback today — acceptable; OS will surface if browser missing */ }
    }
}

public sealed class SourceKindFilter
{
    public required string Key { get; init; }
    public required string Label { get; init; }
}

public sealed class SizeFilter
{
    public required string Key { get; init; }     // category key, "any", or "unknown"
    public required string Label { get; init; }
}

public partial class SourcesViewModel : ObservableObject
{
    private readonly ICompanyRepository _companies;
    private readonly IDiscoverySeedRepository _seeds;
    private readonly IAggregatorRepository _aggregators;

    public ObservableCollection<SourceRow> Rows { get; } = new();
    public ICollectionView View { get; }

    public IReadOnlyList<SourceKindFilter> KindOptions { get; } = new[]
    {
        new SourceKindFilter { Key = "any",       Label = "Any type" },
        new SourceKindFilter { Key = "company",   Label = "Companies" },
        new SourceKindFilter { Key = "directory", Label = "Directories" },
        new SourceKindFilter { Key = "board",     Label = "Boards" },
    };

    public IReadOnlyList<SizeFilter> SizeOptions { get; } = BuildSizeOptions();

    [ObservableProperty]
    private SourceKindFilter? _selectedKind;

    [ObservableProperty]
    private SizeFilter? _selectedSize;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _summaryLine = "";

    public SourcesViewModel(ICompanyRepository companies, IDiscoverySeedRepository seeds,
                            IAggregatorRepository aggregators)
    {
        _companies = companies;
        _seeds = seeds;
        _aggregators = aggregators;

        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = FilterRow;
        // Stable sort: companies first (alpha), then directories, then boards.
        View.SortDescriptions.Add(new SortDescription(nameof(SourceRow.Kind), ListSortDirection.Ascending));
        View.SortDescriptions.Add(new SortDescription(nameof(SourceRow.Name), ListSortDirection.Ascending));

        SelectedKind = KindOptions[0];
        SelectedSize = SizeOptions[0];

        Refresh();
    }

    [RelayCommand]
    public void Refresh()
    {
        Rows.Clear();

        foreach (var c in _companies.GetCompanySourceRows())
        {
            // Prefer careers URL — that's what the user actually wants to open. Falls back to
            // the website URL when the careers page is unknown, and finally to https://{domain}
            // so every row is at least pointing somewhere.
            var url = !string.IsNullOrWhiteSpace(c.CareersUrl) ? c.CareersUrl
                    : !string.IsNullOrWhiteSpace(c.WebsiteUrl) ? c.WebsiteUrl
                    : !string.IsNullOrWhiteSpace(c.Domain)     ? $"https://{c.Domain}"
                    : null;
            var bits = new List<string>();
            if (!string.IsNullOrWhiteSpace(c.City)) bits.Add(c.City!);
            if (!string.IsNullOrWhiteSpace(c.SourceNames)) bits.Add($"from {c.SourceNames}");
            Rows.Add(new SourceRow
            {
                Kind = "company",
                KindLabel = "Company",
                Name = c.Name,
                Url = url,
                Secondary = bits.Count > 0 ? string.Join(" · ", bits) : null,
                SizeCategory = c.SizeCategory,
                IsActive = c.IsActive,
            });
        }

        foreach (var s in _seeds.GetAll())
        {
            Rows.Add(new SourceRow
            {
                Kind = "directory",
                KindLabel = "Directory",
                Name = s.Name,
                Url = s.Url,
                Secondary = string.IsNullOrWhiteSpace(s.Description) ? $"{s.MaxPages} page(s)" : s.Description,
                IsActive = s.IsEnabled,
            });
        }

        foreach (var a in _aggregators.GetAll())
        {
            Rows.Add(new SourceRow
            {
                Kind = "board",
                KindLabel = "Board",
                Name = a.Name,
                Url = a.BaseUrl,
                Secondary = string.IsNullOrWhiteSpace(a.Notes) ? $"{a.MaxPages} page(s)" : a.Notes,
                IsActive = a.IsEnabled,
            });
        }

        RefreshSummary();
    }

    partial void OnSelectedKindChanged(SourceKindFilter? value)  { View.Refresh(); RefreshSummary(); }
    partial void OnSelectedSizeChanged(SizeFilter? value)        { View.Refresh(); RefreshSummary(); }
    partial void OnSearchTextChanged(string value)               { View.Refresh(); RefreshSummary(); }

    private bool FilterRow(object o)
    {
        if (o is not SourceRow r) return false;
        if (SelectedKind is not null && SelectedKind.Key != "any" && r.Kind != SelectedKind.Key) return false;

        if (SelectedSize is not null && SelectedSize.Key != "any")
        {
            if (r.Kind != "company") return false; // size filter only meaningful for companies
            var actual = r.SizeCategory ?? "unknown";
            if (actual != SelectedSize.Key) return false;
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var needle = SearchText.Trim();
            var inName = r.Name?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
            var inUrl  = r.Url?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
            var inSec  = r.Secondary?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
            if (!(inName || inUrl || inSec)) return false;
        }
        return true;
    }

    private void RefreshSummary()
    {
        var shown = Rows.Count == 0 ? 0 : ((ListCollectionView)View).Count;
        SummaryLine = $"Showing {shown} of {Rows.Count} sources";
    }

    private static IReadOnlyList<SizeFilter> BuildSizeOptions()
    {
        var list = new List<SizeFilter>
        {
            new SizeFilter { Key = "any",     Label = "Any size" },
        };
        foreach (var k in CompanySizeCategories.All)
            list.Add(new SizeFilter { Key = k, Label = CompanySizeCategories.Label(k) });
        list.Add(new SizeFilter { Key = "unknown", Label = "Unknown size" });
        return list;
    }
}
