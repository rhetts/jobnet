using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Jobnet.Services.AtsAdapters;

namespace Jobnet.ViewModels;

/// <summary>
/// Backs the Parser Report screen. Lists every company with which extraction system is in use
/// (native ATS / cached selectors / AI extract / unknown), the health of the cached selector
/// profile, and lets the user force a re-derive or disable the selector path per company.
/// </summary>
public partial class ParserReportViewModel : ObservableObject
{
    private readonly ICompanyRepository _companies;
    private readonly IJobRefresher _refresher;

    /// <summary>Company IDs with an in-flight Re-derive. Used so a full-list <see cref="Refresh"/>
    /// doesn't wipe the per-row busy spinner — the new row instance picks up the busy flag from
    /// here when it's reconstructed.</summary>
    private readonly HashSet<int> _busyIds = new();

    public ObservableCollection<ParserReportRow> AllRows { get; } = new();
    public ObservableCollection<ParserReportRow> Rows { get; } = new();

    /// <summary>Filter chip — "(any)" shows everything, the rest match ParserSystem strings.</summary>
    public IReadOnlyList<string> SystemFilters { get; } =
        new[] { "(any)", "native ATS", "selectors", "AI extract", "unknown" };

    public IReadOnlyList<string> StatusFilters { get; } =
        new[] { "(any)", "ok", "drift", "error", "no profile" };

    [ObservableProperty] private string _selectedSystem = "(any)";
    [ObservableProperty] private string _selectedStatus = "(any)";
    [ObservableProperty] private string _summary = "";

    public ParserReportViewModel(ICompanyRepository companies, IJobRefresher refresher)
    {
        _companies = companies;
        _refresher = refresher;
        Refresh();
    }

    [RelayCommand]
    public void Refresh()
    {
        AllRows.Clear();
        foreach (var c in _companies.GetAll())
        {
            var row = new ParserReportRow(c);
            // Carry over in-flight busy state so a full reload doesn't drop the spinner.
            if (_busyIds.Contains(c.Id)) row.IsBusy = true;
            AllRows.Add(row);
        }

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

    /// <summary>Clear the cached profile and immediately re-scan this single company. The scan
    /// goes through the normal JobRefresher.RefreshAsync path, so it will hit native ATS first
    /// if applicable, then JSON-LD, then attempt AI extraction + selector derivation. The row
    /// shows a spinner while in flight and updates in-place when the scan completes.</summary>
    [RelayCommand]
    private async Task ReDeriveAsync(ParserReportRow? row)
    {
        if (row is null || row.IsBusy) return;
        var company = _companies.GetById(row.Id);
        if (company is null) return;

        row.IsBusy = true;
        row.BusyText = "Re-deriving...";
        _busyIds.Add(row.Id);
        try
        {
            // Clear so the AI-extraction path treats this as a fresh derivation.
            _companies.ClearParserStrategy(row.Id);
            // Refetch the company so the in-memory model reflects the cleared profile.
            company = _companies.GetById(row.Id);
            if (company is null) return;

            await Task.Run(() => _refresher.RefreshAsync(company)).ConfigureAwait(true);

            // Pull the freshly-updated row from the DB and swap it in place.
            var updated = _companies.GetById(row.Id);
            if (updated is not null)
            {
                var idx = AllRows.IndexOf(row);
                if (idx >= 0) AllRows[idx] = new ParserReportRow(updated);
                ApplyFilters();
            }
        }
        catch (Exception ex)
        {
            // Surface the error in the row's busy text — the user otherwise sees nothing happen.
            row.IsBusy = false;
            row.BusyText = $"Failed: {ex.Message}";
        }
        finally
        {
            _busyIds.Remove(row.Id);
            // If we already replaced the row above, row.IsBusy on the old instance is irrelevant.
            // If we hit the catch branch, IsBusy is already false. No further cleanup needed.
        }
    }

    [RelayCommand]
    private void ToggleDisabled(ParserReportRow? row)
    {
        if (row is null) return;
        _companies.SetParserStrategyDisabled(row.Id, !row.IsDisabled);
        Refresh();
    }
}

/// <summary>One row on the Parser Report. Most fields are computed once from a Company snapshot;
/// <see cref="IsBusy"/> and <see cref="BusyText"/> are observable so the row can show progress
/// while a per-company Re-derive is in flight.</summary>
public partial class ParserReportRow : ObservableObject
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

    /// <summary>True while a Re-derive is running for this company. Buttons in the row bind
    /// IsEnabled to <see cref="NotIsBusy"/>; the row also shows <see cref="BusyText"/> as a
    /// status string.</summary>
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string? _busyText;

    public bool NotIsBusy => !IsBusy;
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(NotIsBusy));

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
