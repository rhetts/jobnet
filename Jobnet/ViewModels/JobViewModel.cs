using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jobnet.Models;

namespace Jobnet.ViewModels;

public partial class JobViewModel : ObservableObject
{
    public Job Job { get; }
    public string CompanyName { get; }
    public string? CompanyCity { get; }
    public int CompositeScore { get; }
    public string LevelName { get; }
    public string AreasDisplay { get; }
    public string ClassificationLine { get; }

    /// <summary>True when this job is posted by a recruitment agency, not a direct employer.
    /// Drives the "Agency" chip on the job card and the "Hide agency postings" filter.</summary>
    public bool IsAgency { get; }

    /// <summary>Display names of technologies detected in this job's text. Rendered as chips
    /// on the job card. Empty when nothing matched.</summary>
    public IReadOnlyList<string> Technologies { get; }

    /// <summary>Technology IDs matching the Technologies display list, in the same order.
    /// Used by the filter to check whether the job has any of the user-selected tech IDs
    /// without round-tripping through display strings.</summary>
    public IReadOnlyList<int> TechnologyIds { get; }

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isIdJustCopied;

    /// <summary>How many of the current search-box tokens were found in this job's text.
    /// Set externally by <c>MainWindowViewModel</c> whenever the search box changes; the
    /// JobsView sort uses this so jobs matching more keywords float up. Zero when there's
    /// no search active.</summary>
    [ObservableProperty]
    private int _keywordMatchCount;

    [ObservableProperty]
    private bool _isApplied;

    /// <summary>Invoked when the user toggles the Applied checkbox on the card. Wired by MainWindowViewModel.</summary>
    private Action<JobViewModel, bool>? _onAppliedToggled;

    /// <summary>Invoked when the user up/down-votes via the card buttons. Wired by MainWindowViewModel.
    /// Passes the new InterestLevel so the parent can persist + refresh sort.</summary>
    private Action<JobViewModel, InterestLevel>? _onInterestChanged;

    /// <summary>Invoked when the user clicks Expired on an approved card — unapproves the job
    /// AND marks it removed in the DB. Wired by MainWindowViewModel.</summary>
    private Action<JobViewModel>? _onMarkExpired;

    /// <summary>Briefly flag the ID as copied so the UI can confirm. Auto-resets after ~1.5s.</summary>
    public async System.Threading.Tasks.Task FlashCopiedAsync()
    {
        IsIdJustCopied = true;
        await System.Threading.Tasks.Task.Delay(1500);
        IsIdJustCopied = false;
    }

    public JobViewModel(Job job, string companyName, string? companyCity, int compositeScore,
                         string? levelName = null, IEnumerable<string>? areaNames = null,
                         Action<JobViewModel, bool>? onAppliedToggled = null,
                         Action<JobViewModel, InterestLevel>? onInterestChanged = null,
                         IEnumerable<string>? technologyNames = null,
                         IEnumerable<int>? technologyIds = null,
                         bool isAgency = false,
                         Action<JobViewModel>? onMarkExpired = null)
    {
        Job = job;
        CompanyName = companyName;
        CompanyCity = companyCity;
        CompositeScore = compositeScore;
        LevelName = string.IsNullOrWhiteSpace(levelName) ? "Unclassified" : levelName!;
        var areas = (areaNames ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        AreasDisplay = areas.Count == 0 ? "—" : string.Join(", ", areas);
        ClassificationLine = $"Level: {LevelName}   ·   Areas: {AreasDisplay}";
        IsAgency = isAgency;
        Technologies = (technologyNames ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        TechnologyIds = (technologyIds ?? Array.Empty<int>()).ToList();

        // Initialise via the backing field so the partial change handler doesn't fire on construction.
        _isApplied = job.DateApplied is not null;
        _onAppliedToggled = onAppliedToggled;
        _onInterestChanged = onInterestChanged;
        _onMarkExpired = onMarkExpired;
    }

    partial void OnIsAppliedChanged(bool value)
    {
        _onAppliedToggled?.Invoke(this, value);
        OnPropertyChanged(nameof(AppliedSortKey));
        OnPropertyChanged(nameof(AppliedLabel));
    }

    /// <summary>Button label that flips with the IsApplied state.</summary>
    public string AppliedLabel => IsApplied ? "✓ Applied" : "Mark applied";

    /// <summary>Sort key for the "All jobs" tab: 0 = active/neutral (top), 1 = downvoted (bottom).
    /// Approved jobs are filtered out of that tab entirely so they never appear here.</summary>
    public int VoteOrder => Job.InterestLevel == InterestLevel.NotInteresting ? 1 : 0;

    public bool IsApproved  => Job.InterestLevel == InterestLevel.Approved;
    public bool IsDownvoted => Job.InterestLevel == InterestLevel.NotInteresting;

    /// <summary>Sort key for the "Approved jobs" tab: not-applied (0) above applied (1). Within
    /// each group, secondary sort comes from SortKey (resume match or composite score).</summary>
    public int AppliedSortKey => IsApplied ? 1 : 0;

    /// <summary>Tab 1 action: flip a job to Approved (or back to Neutral if already approved).
    /// Moves the job to the Approved-jobs tab.</summary>
    [RelayCommand]
    private void Approve()
    {
        var next = IsApproved ? InterestLevel.Neutral : InterestLevel.Approved;
        Job.InterestLevel = next;
        OnPropertyChanged(nameof(InterestLevel));
        OnPropertyChanged(nameof(InterestGlyph));
        OnPropertyChanged(nameof(IsApproved));
        OnPropertyChanged(nameof(IsDownvoted));
        OnPropertyChanged(nameof(VoteOrder));
        _onInterestChanged?.Invoke(this, next);
    }

    [RelayCommand]
    private void Downvote()
    {
        var next = IsDownvoted ? InterestLevel.Neutral : InterestLevel.NotInteresting;
        Job.InterestLevel = next;
        OnPropertyChanged(nameof(InterestLevel));
        OnPropertyChanged(nameof(InterestGlyph));
        OnPropertyChanged(nameof(IsApproved));
        OnPropertyChanged(nameof(IsDownvoted));
        OnPropertyChanged(nameof(VoteOrder));
        _onInterestChanged?.Invoke(this, next);
    }

    /// <summary>Tab 2 action: toggle the Applied state. Sorting puts applied jobs at the bottom
    /// of the Approved list — they're "done" in the user's pipeline view. The IsApplied setter
    /// (source-generated) invokes OnIsAppliedChanged, which fires the persist callback AND raises
    /// PropertyChanged for AppliedSortKey + AppliedLabel — no need to duplicate those here.</summary>
    [RelayCommand]
    private void ToggleApplied() => IsApplied = !IsApplied;

    /// <summary>Tab 2 action: mark this approved job as expired. Unapproves it AND flags it
    /// inactive/removed so it drops off the Approved view entirely.</summary>
    [RelayCommand]
    private void MarkExpired() => _onMarkExpired?.Invoke(this);

    public string Title => Job.Title;
    public string JobIdDisplay => $"#{Job.Id}";
    public InterestLevel InterestLevel => Job.InterestLevel;
    public bool IsActive => Job.IsActive;

    public int? ResumeMatchScore => Job.ResumeMatchScore;
    public bool HasResumeMatch => Job.ResumeMatchScore.HasValue;
    public string ResumeMatchDisplay => Job.ResumeMatchScore.HasValue ? $"Match {Job.ResumeMatchScore.Value}" : "";
    public string ResumeMatchReason => Job.ResumeMatchReason ?? "";
    /// <summary>Used by the JobsView sort: resume match descending. Jobs without a resume match
    /// yet (never scored, or scoring failed) sink below any real match score rather than falling
    /// back to composite score — match is now the only sort signal shown or used.</summary>
    public int SortKey => Job.ResumeMatchScore ?? -1;

    /// <summary>Glyph for the expand/collapse toggle.</summary>
    public string ExpandGlyph => IsExpanded ? "▾" : "▸";
    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandGlyph));

    /// <summary>What to show in the expanded section: AI summary if present, else a cleaned
    /// version of the description snippet (HTML stripped, company intro skipped), else placeholder.</summary>
    public string ExpandedText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Job.Summary)) return Job.Summary!;
            if (!string.IsNullOrWhiteSpace(Job.DescriptionSnippet))
                return Jobnet.Services.JobSources.SnippetCleaner.Clean(Job.DescriptionSnippet, maxChars: 600)
                       ?? "(no summary available — try 'Generate summaries' from Refresh)";
            return "(no summary available — try 'Generate summaries' from Refresh)";
        }
    }
    public bool HasSummary => !string.IsNullOrWhiteSpace(Job.Summary);

    /// <summary>Job's posted location, or company HQ city as fallback, with the trailing country
    /// token stripped (e.g. "Vancouver, BC, Canada" → "Vancouver, BC").</summary>
    public string CityDisplay =>
        StripCountry(!string.IsNullOrWhiteSpace(Job.Location) ? Job.Location! : (CompanyCity ?? ""));
    public bool HasCity => !string.IsNullOrWhiteSpace(CityDisplay);

    // Matches a comma-preceded country name/abbreviation, as long as it isn't glued to another
    // word (so "Canada-Remote" / "US-Remote" survive untouched — those describe a work mode, not
    // a plain trailing country). Deliberately excludes bare "CA", which is ambiguous with California.
    private static readonly Regex CountrySuffixRe = new(
        @",\s*(Canada|United States|United Kingdom|USA|US|CAN|UK)(?![A-Za-z0-9-])",
        RegexOptions.IgnoreCase);

    private static string StripCountry(string location) =>
        string.IsNullOrEmpty(location) ? location : CountrySuffixRe.Replace(location, "").Trim();

    /// <summary>City line + posting age + on-site/employment type, all on one line. Replaces the
    /// old separate MetaLine row — remote/employment type is only appended when it was actually
    /// extracted (not "unknown"), since most sources never fill it in. Composite score is
    /// deliberately not shown here — it reads as a personal-fit claim ("you're a 100% match")
    /// when it's really just a seniority/category sort key; resume match is the real fit signal.</summary>
    public string CityMetaDisplay
    {
        get
        {
            var parts = new List<string>();
            if (HasCity) parts.Add(CityDisplay);
            parts.Add($"{FormatAge(Job.DateFirstSeen)} old");
            var type = OnSiteFullTimeDisplay();
            if (!string.IsNullOrEmpty(type)) parts.Add(type);
            return string.Join(" · ", parts);
        }
    }

    private string OnSiteFullTimeDisplay()
    {
        var remoteKnown = !string.IsNullOrWhiteSpace(Job.RemoteType) && !string.Equals(Job.RemoteType, "unknown", StringComparison.OrdinalIgnoreCase);
        var empKnown = !string.IsNullOrWhiteSpace(Job.EmploymentType) && !string.Equals(Job.EmploymentType, "unknown", StringComparison.OrdinalIgnoreCase);
        if (remoteKnown && empKnown) return $"{Capitalize(Job.RemoteType!)} · {Capitalize(Job.EmploymentType!)}";
        if (remoteKnown) return Capitalize(Job.RemoteType!);
        if (empKnown) return Capitalize(Job.EmploymentType!);
        return "";
    }

    /// <summary>First comma-separated token of the location, used for city filtering and bucketing.
    /// Falls back to company HQ city, then "Unknown".</summary>
    public string NormalizedCity
    {
        get
        {
            var src = !string.IsNullOrWhiteSpace(Job.Location) ? Job.Location! : CompanyCity;
            if (string.IsNullOrWhiteSpace(src)) return "Unknown";
            var first = src.Split(new[] { ',', '/', ';' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            return first.Length == 0 ? "Unknown" : first;
        }
    }

    public string InterestGlyph => InterestLevel switch
    {
        InterestLevel.Approved    => "★",
        InterestLevel.NotInteresting => "✗",
        _                            => " "
    };

    public string SalaryDisplay => FormatSalary();

    private string FormatSalary()
    {
        if (Job.SalaryMin is null && Job.SalaryMax is null) return "";
        var cur = Job.SalaryCurrency ?? "";
        var min = Job.SalaryMin;
        var max = Job.SalaryMax;
        string range;
        if (min.HasValue && max.HasValue && min == max) range = Money(min.Value);
        else if (min.HasValue && max.HasValue)          range = $"{Money(min.Value)}–{Money(max.Value)}";
        else if (min.HasValue)                          range = $"{Money(min.Value)}+";
        else                                            range = $"≤{Money(max!.Value)}";
        var period = Job.SalaryPeriod switch { "hour" => "/hr", "month" => "/mo", "year" => "/yr", _ => "" };
        return string.IsNullOrEmpty(cur) ? $"{range}{period}" : $"{cur} {range}{period}";
    }

    private static string Money(int v)
    {
        if (v >= 1000) return $"${v / 1000}K";
        return $"${v}";
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];

    private static string FormatAge(DateTime when)
    {
        var days = (int)Math.Floor((DateTime.UtcNow - when).TotalDays);
        return days switch
        {
            < 1   => "today",
            1     => "1 day",
            < 30  => $"{days} days",
            < 60  => "1 month",
            < 365 => $"{days / 30} months",
            _     => $"{days / 365} year{(days / 365 == 1 ? "" : "s")}"
        };
    }
}
