using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Jobnet.Services.Filters;

namespace Jobnet.ViewModels;

/// <summary>
/// Backs the Filters screen — the one place to see and edit every block/allow rule in the app.
/// Before this existed the only way to inspect filter_rule was raw SQL.
///
/// The Add flow deliberately dry-runs before it saves. Writing regex against a live URL cache is
/// where the damage happens: during the initial seed, '/legal' looked obviously safe and would
/// have killed Y Combinator's legal-roles job board, and four host rules would have silently
/// broken the refresh for active companies (Vanta sits on account.ycombinator.com). Both were
/// caught only by replaying the pattern against real data first, which is what Preview does.
/// </summary>
public partial class FiltersViewModel : ObservableObject
{
    private readonly IFilterRuleRepository _rules;
    private readonly ICompanyUrlsRepository _urls;
    private readonly ICompanyRepository _companies;
    private readonly FilterRuleProvider _provider;

    public ObservableCollection<FilterRuleRowVm> AllRows { get; } = new();
    public ObservableCollection<FilterRuleRowVm> Rows { get; } = new();

    public IReadOnlyList<string> SubjectFilters { get; } =
        new[] { "(any)", FilterSubject.Host, FilterSubject.Url, FilterSubject.JobTitle,
                FilterSubject.CompanyName, FilterSubject.Location, FilterSubject.SearchToken };

    public IReadOnlyList<string> ShowFilters { get; } =
        new[] { "(all)", "enabled only", "disabled only", "never fired" };

    // Choices for the add row.
    public IReadOnlyList<string> Subjects { get; } =
        new[] { FilterSubject.Host, FilterSubject.Url, FilterSubject.JobTitle,
                FilterSubject.CompanyName, FilterSubject.Location, FilterSubject.SearchToken };
    public IReadOnlyList<string> Actions { get; } =
        new[] { FilterAction.Block, FilterAction.Allow, FilterAction.Greylist, FilterAction.Boost };
    public IReadOnlyList<string> MatchTypes { get; } =
        new[] { FilterMatchType.Domain, FilterMatchType.Regex, FilterMatchType.Substring,
                FilterMatchType.Exact, FilterMatchType.Word };
    public IReadOnlyList<string> Scopes { get; } =
        new[] { "(everywhere)", FilterScope.Crawl, FilterScope.Discovery };

    [ObservableProperty] private string _selectedSubjectFilter = "(any)";
    [ObservableProperty] private string _selectedShowFilter = "(all)";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _compileErrors = "";

    // ── add-a-rule form ────────────────────────────────────────────────────
    [ObservableProperty] private string _newSubject = FilterSubject.Host;
    [ObservableProperty] private string _newAction = FilterAction.Block;
    [ObservableProperty] private string _newMatchType = FilterMatchType.Domain;
    [ObservableProperty] private string _newScope = "(everywhere)";
    [ObservableProperty] private string _newPattern = "";
    [ObservableProperty] private string _newNote = "";
    [ObservableProperty] private string _previewText = "";
    [ObservableProperty] private bool _previewIsWarning;

    // ── test-a-url box ─────────────────────────────────────────────────────
    [ObservableProperty] private string _testUrl = "";
    [ObservableProperty] private string _testResult = "";

    public FiltersViewModel(IFilterRuleRepository rules, ICompanyUrlsRepository urls,
                             ICompanyRepository companies, FilterRuleProvider provider)
    {
        _rules = rules;
        _urls = urls;
        _companies = companies;
        _provider = provider;
        Refresh();
    }

    [RelayCommand]
    public void Refresh()
    {
        AllRows.Clear();
        foreach (var r in _rules.GetAll())
            AllRows.Add(new FilterRuleRowVm(r));

        // Surface any pattern that won't compile — a rule that looks active but never fires is
        // worse than no rule at all.
        var errs = FilterRuleSet.FromRules(_rules.GetEnabled()).CompileErrors;
        CompileErrors = errs.Count == 0 ? "" : $"{errs.Count} rule(s) failed to compile: {string.Join("; ", errs)}";

        ApplyFilters();
    }

    private void ApplyFilters()
    {
        var q = AllRows.AsEnumerable();
        if (SelectedSubjectFilter != "(any)") q = q.Where(r => r.Subject == SelectedSubjectFilter);
        q = SelectedShowFilter switch
        {
            "enabled only"  => q.Where(r => r.IsEnabled),
            "disabled only" => q.Where(r => !r.IsEnabled),
            "never fired"   => q.Where(r => r.HitCount == 0),
            _               => q,
        };
        var list = q.ToList();

        Rows.Clear();
        foreach (var r in list) Rows.Add(r);

        Summary = $"Showing {Rows.Count} of {AllRows.Count} rules  ·  "
                + $"{AllRows.Count(r => !r.IsEnabled)} disabled  ·  "
                + $"{AllRows.Count(r => r.HitCount == 0)} never fired";
    }

    partial void OnSelectedSubjectFilterChanged(string value) => ApplyFilters();
    partial void OnSelectedShowFilterChanged(string value) => ApplyFilters();
    partial void OnNewPatternChanged(string value) => PreviewText = "";

    /// <summary>Build the candidate rule from the add-form. Id 0 — it isn't saved yet.</summary>
    private FilterRule? BuildCandidate()
    {
        if (string.IsNullOrWhiteSpace(NewPattern)) return null;
        return new FilterRule
        {
            Subject = NewSubject,
            Action = NewAction,
            MatchType = NewMatchType,
            Pattern = NewPattern.Trim(),
            Scope = NewScope == "(everywhere)" ? null : NewScope,
            Note = string.IsNullOrWhiteSpace(NewNote) ? null : NewNote.Trim(),
            IsEnabled = true,
        };
    }

    /// <summary>Replay the candidate against the real URL cache and company list without saving.
    /// Reports what it would purge and, crucially, what it would break.</summary>
    [RelayCommand]
    private void Preview()
    {
        var candidate = BuildCandidate();
        if (candidate is null) { PreviewText = "Enter a pattern first."; PreviewIsWarning = true; return; }

        var set = FilterRuleSet.FromRules(new[] { candidate });
        if (set.CompileErrors.Count > 0)
        {
            PreviewText = $"Pattern will not compile — {set.CompileErrors[0]}";
            PreviewIsWarning = true;
            return;
        }

        // Which cached URLs this rule would drop. Scope null tests both crawl and discovery.
        var scope = candidate.Scope ?? FilterScope.Crawl;
        var matched = _urls.GetAll()
            .Where(u => candidate.Subject == FilterSubject.Host
                        ? UriHost(u.Url) is { } h && set.IsHostBlocked(h, scope)
                        : set.IsUrlBlocked(u.Url, scope))
            .ToList();

        var everYielded = matched.Where(u => u.LastYielded is not null).ToList();

        // Would this rule block an active company's own domain? That silently kills its refresh.
        var brokenCompanies = _companies.GetAll()
            .Where(c => c.IsActive && !c.IsBlacklisted && !string.IsNullOrWhiteSpace(c.Domain))
            .Where(c => set.IsHostBlocked(c.Domain, scope))
            .ToList();

        var parts = new List<string> { $"would purge {matched.Count} cached URL(s)" };
        if (everYielded.Count > 0)
            parts.Add($"⚠ {everYielded.Count} of them have produced jobs before "
                    + $"(e.g. {everYielded[0].Url})");
        if (brokenCompanies.Count > 0)
            parts.Add($"⚠ blocks the domain of {brokenCompanies.Count} ACTIVE compan(y/ies): "
                    + string.Join(", ", brokenCompanies.Take(4).Select(c => c.Domain)));

        PreviewIsWarning = everYielded.Count > 0 || brokenCompanies.Count > 0;
        PreviewText = string.Join("   ·   ", parts);
    }

    [RelayCommand]
    private void AddRule()
    {
        var candidate = BuildCandidate();
        if (candidate is null) { PreviewText = "Enter a pattern first."; PreviewIsWarning = true; return; }

        var set = FilterRuleSet.FromRules(new[] { candidate });
        if (set.CompileErrors.Count > 0)
        {
            PreviewText = $"Not saved — pattern will not compile: {set.CompileErrors[0]}";
            PreviewIsWarning = true;
            return;
        }

        var duplicate = AllRows.Any(r =>
            string.Equals(r.Pattern, candidate.Pattern, StringComparison.OrdinalIgnoreCase)
            && r.Subject == candidate.Subject
            && r.Action == candidate.Action
            && string.Equals(r.RawScope, candidate.Scope, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
        {
            PreviewText = "Not saved — an identical rule already exists.";
            PreviewIsWarning = true;
            return;
        }

        _rules.Insert(candidate);
        _provider.Invalidate();
        NewPattern = "";
        NewNote = "";
        PreviewText = $"Added. Cached URLs matching it are purged on the next refresh.";
        PreviewIsWarning = false;
        Refresh();
    }

    [RelayCommand]
    private void ToggleEnabled(FilterRuleRowVm? row)
    {
        if (row is null) return;
        _rules.SetEnabled(row.Id, !row.IsEnabled);
        _provider.Invalidate();
        Refresh();
    }

    [RelayCommand]
    private void DeleteRule(FilterRuleRowVm? row)
    {
        if (row is null) return;
        _rules.Delete(row.Id);
        _provider.Invalidate();
        Refresh();
    }

    /// <summary>Paste a URL, find out which rule (if any) blocks it. Uses ExplainUrl so testing
    /// doesn't inflate the hit counters.</summary>
    [RelayCommand]
    private void RunTest()
    {
        if (string.IsNullOrWhiteSpace(TestUrl)) { TestResult = ""; return; }

        var set = FilterRuleSet.FromRules(_rules.GetEnabled());
        var url = TestUrl.Trim();

        var crawlHit = set.ExplainUrl(url, FilterScope.Crawl);
        var discoveryHit = set.ExplainUrl(url, FilterScope.Discovery);

        if (crawlHit is null && discoveryHit is null)
        {
            TestResult = "ALLOWED — no rule matches this URL.";
            return;
        }
        var lines = new List<string>();
        if (crawlHit is not null)
            lines.Add($"BLOCKED when crawling — rule #{crawlHit.Id} [{crawlHit.Subject}/{crawlHit.MatchType}] {crawlHit.Pattern}"
                    + (string.IsNullOrWhiteSpace(crawlHit.Note) ? "" : $"  ({crawlHit.Note})"));
        if (discoveryHit is not null)
            lines.Add($"BLOCKED in discovery — rule #{discoveryHit.Id} [{discoveryHit.Subject}/{discoveryHit.MatchType}] {discoveryHit.Pattern}");
        TestResult = string.Join("\n", lines);
    }

    private static string? UriHost(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : null;
}

/// <summary>One row of the Filters grid.</summary>
public sealed class FilterRuleRowVm
{
    public int Id { get; }
    public string Subject { get; }
    public string Action { get; }
    public string MatchType { get; }
    public string Pattern { get; }
    public string ScopeDisplay { get; }
    public string? RawScope { get; }
    public string? Note { get; }
    public bool IsEnabled { get; }
    public int HitCount { get; }
    public string LastHitDisplay { get; }
    public string EnabledToggleLabel { get; }
    public string RowBrush { get; }

    public FilterRuleRowVm(FilterRule r)
    {
        Id = r.Id;
        Subject = r.Subject;
        Action = r.Action;
        MatchType = r.MatchType;
        Pattern = r.Pattern;
        RawScope = r.Scope;
        ScopeDisplay = r.Scope ?? "everywhere";
        Note = r.Note;
        IsEnabled = r.IsEnabled;
        HitCount = r.HitCount;
        LastHitDisplay = r.LastHit is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—";
        EnabledToggleLabel = r.IsEnabled ? "Disable" : "Enable";
        RowBrush = !r.IsEnabled ? "#AAA"
                 : r.Action == FilterAction.Allow ? "#2A8F4F"
                 : "#333";
    }
}
