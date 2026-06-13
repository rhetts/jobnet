using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Jobnet.Services.Parsing.HtmlPatternParsers;

namespace Jobnet.ViewModels;

/// <summary>
/// Backs the Parser Report screen. Lists every company with the extraction system actually in
/// use (native ATS / hand-written parser / cached selectors / AI extract / unknown), plus the
/// health of any cached selector profile and a per-row disable toggle.
/// </summary>
public partial class ParserReportViewModel : ObservableObject
{
    private readonly ICompanyRepository _companies;
    private readonly HtmlPatternRegistry _parserRegistry;

    public ObservableCollection<ParserReportRow> AllRows { get; } = new();
    public ObservableCollection<ParserReportRow> Rows { get; } = new();

    /// <summary>Filter chip — "(any)" shows everything, the rest match ParserSystem strings.</summary>
    public IReadOnlyList<string> SystemFilters { get; } =
        new[] { "(any)", "native ATS", "hand-written", "selectors", "AI extract", "unknown" };

    /// <summary>Comma-separated list of hand-written parsers registered in DI. Surfaced in the
    /// window header so the user can see what patterns are wired in without digging into code.</summary>
    public string RegisteredParsersDisplay { get; }

    public IReadOnlyList<string> StatusFilters { get; } =
        new[] { "(any)", "ok", "drift", "error", "no profile" };

    [ObservableProperty] private string _selectedSystem = "(any)";
    [ObservableProperty] private string _selectedStatus = "(any)";
    [ObservableProperty] private string _summary = "";

    public ParserReportViewModel(ICompanyRepository companies, HtmlPatternRegistry parserRegistry)
    {
        _companies = companies;
        _parserRegistry = parserRegistry;
        RegisteredParsersDisplay = _parserRegistry.Names.Count == 0
            ? "(none registered)"
            : string.Join(", ", _parserRegistry.Names);
        Refresh();
    }

    [RelayCommand]
    public void Refresh()
    {
        AllRows.Clear();
        foreach (var c in _companies.GetAll())
            AllRows.Add(new ParserReportRow(c));

        ApplyFilters();
    }

    private void ApplyFilters()
    {
        var q = AllRows.AsEnumerable();
        if (SelectedSystem != "(any)") q = q.Where(r => r.ParserSystem == SelectedSystem);
        if (SelectedStatus != "(any)") q = q.Where(r => r.StatusBadge == SelectedStatus);
        var list = q.ToList();

        Rows.Clear();
        foreach (var r in list) Rows.Add(r);

        var counts = AllRows.GroupBy(r => r.ParserSystem)
                            .ToDictionary(g => g.Key, g => g.Count());
        Summary = $"Showing {Rows.Count} of {AllRows.Count}  ·  "
                + string.Join("  ·  ", counts.Select(kv => $"{kv.Key}: {kv.Value}"));
    }

    partial void OnSelectedSystemChanged(string value) => ApplyFilters();
    partial void OnSelectedStatusChanged(string value) => ApplyFilters();

    [RelayCommand]
    private void ToggleDisabled(ParserReportRow? row)
    {
        if (row is null) return;
        _companies.SetParserStrategyDisabled(row.Id, !row.IsDisabled);
        Refresh();
    }
}

/// <summary>One row on the Parser Report. All fields are computed once from a Company snapshot;
/// the row is replaced wholesale on Refresh rather than mutated in place.</summary>
public sealed class ParserReportRow
{
    public int Id { get; }
    public string Name { get; }
    public string Domain { get; }

    /// <summary>Which extraction system this company will actually use on next refresh.
    /// Order of precedence in JobRefresher: native ATS (ats_type + slug set) → cached selectors
    /// (parser_strategy non-null and not disabled) → AI extract (everything else).</summary>
    public string ParserSystem { get; }

    public string StatusBadge { get; }
    public string StatusBrush { get; }

    public string DerivedDisplay { get; }
    public string LastResultDisplay { get; }
    public string? LastError { get; }
    public bool IsDisabled { get; }
    public string DisabledToggleLabel { get; }

    public ParserReportRow(Company c)
    {
        Id = c.Id;
        Name = c.Name;
        Domain = c.Domain;

        IsDisabled = c.ParserStrategyDisabled;
        DisabledToggleLabel = c.ParserStrategyDisabled ? "Enable" : "Disable";

        if (!string.IsNullOrEmpty(c.AtsType) && !string.IsNullOrEmpty(c.AtsSlug))
        {
            ParserSystem = "native ATS";
            StatusBadge = c.AtsType!;
            StatusBrush = "#2A8F4F";
        }
        // Hand-written parser attribution takes precedence over selector profiles / AI extract,
        // since a positive parser match on the last refresh means the company is *actually*
        // being served by a hand-coded pattern right now, not whatever profile was cached earlier.
        else if (!string.IsNullOrWhiteSpace(c.LastCompanyParser))
        {
            ParserSystem = "hand-written";
            StatusBadge = c.LastCompanyParser!;
            StatusBrush = "#1f9d55";   // green — these are the cheapest, most reliable matches
        }
        else if (c.ParserStrategyDisabled)
        {
            ParserSystem = "AI extract";
            StatusBadge = "manual override";
            StatusBrush = "#888";
        }
        else if (!string.IsNullOrWhiteSpace(c.ParserStrategy))
        {
            ParserSystem = "selectors";
            StatusBadge = c.ParserStrategyLastResult ?? "ok";
            StatusBrush = StatusBadge switch
            {
                "ok"    => "#2A8F4F",
                "drift" => "#E08E0B",
                "error" => "#C44",
                _       => "#1976D2",
            };
        }
        else if (c.DateLastScan is null)
        {
            ParserSystem = "unknown";
            StatusBadge = "never scanned";
            StatusBrush = "#888";
        }
        else
        {
            ParserSystem = "AI extract";
            StatusBadge = "no profile";
            StatusBrush = "#1976D2";
        }

        DerivedDisplay = c.ParserStrategyDerivedAt is { } d
            ? d.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "—";

        LastResultDisplay = c.ParserStrategyLastResultAt is { } r
            ? $"{c.ParserStrategyLastResult ?? "—"} ({r.ToLocalTime():yyyy-MM-dd HH:mm})"
            : "—";

        LastError = c.ParserStrategyLastError;
    }
}
