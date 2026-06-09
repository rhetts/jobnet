using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jobnet.Models;
using Jobnet.Data.Repositories;
using Jobnet.Services;
using Jobnet.Services.AtsAdapters;
using Jobnet.Services.Discovery;
using Jobnet.Views;

namespace Jobnet.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly IJobDataService _data;
    private readonly IJobRepository? _jobsRepo;
    private readonly IDiscoveryService? _discovery;
    private readonly IJobRefresher? _refresher;
    private readonly Func<SettingsWindow>? _settingsWindowFactory;
    private readonly Func<CompanyProfileWindow>? _profileWindowFactory;
    private readonly Func<RefreshWindow>? _refreshWindowFactory;
    private readonly Func<SavedFiltersWindow>? _savedFiltersWindowFactory;
    private readonly ISavedFilterRepository? _savedFilters;
    private readonly Func<ResumeWindow>? _resumeWindowFactory;
    private readonly Func<ServiceLimitsWindow>? _limitsWindowFactory;
    private readonly Func<RunsWindow>? _runsWindowFactory;
    private readonly Func<ParserReportWindow>? _parserReportWindowFactory;
    private readonly Func<CoverLetterWindow>? _coverLetterWindowFactory;
    private List<JobViewModel> _allJobs = new();

    public ObservableCollection<CompanyViewModel> Companies { get; } = new();
    public ICollectionView CompaniesView { get; }

    public ObservableCollection<JobViewModel> Jobs { get; } = new();
    /// <summary>Tab 1 view — every active job EXCEPT approved (approved live in <see cref="ApprovedJobsView"/>).</summary>
    public ICollectionView JobsView { get; }
    /// <summary>Tab 2 view — only approved jobs, applied ones sorted to bottom.</summary>
    public ICollectionView ApprovedJobsView { get; }
    /// <summary>Live counters for the tab headers.</summary>
    [ObservableProperty] private int _allJobsTabCount;
    [ObservableProperty] private int _approvedJobsTabCount;

    [ObservableProperty]
    private CompanyViewModel? _selectedCompany;

    [ObservableProperty]
    private string _companySearchText = string.Empty;

    [ObservableProperty]
    private bool _showAllCompanies;          // default: only companies with active jobs

    [ObservableProperty]
    private bool _showRemovedJobs;

    [ObservableProperty]
    private bool _includeAppliedJobs;

    [ObservableProperty]
    private bool _hideAgencyJobs;

    [ObservableProperty]
    private string _statusBarText = string.Empty;

    [ObservableProperty]
    private string _jobsPaneHeader = "All Jobs";

    // Job filters (right pane)
    [ObservableProperty]
    private string _jobKeywordFilter = string.Empty;

    [ObservableProperty]
    private string _levelFilterSummary = "Any level";

    [ObservableProperty]
    private string _areaFilterSummary = "Any area";

    [ObservableProperty]
    private string _cityFilterSummary = "Any city";

    [ObservableProperty]
    private string _filteredJobsCountText = string.Empty;

    public ObservableCollection<ResumeMatchThreshold> ResumeMatchThresholds { get; } = new()
    {
        new ResumeMatchThreshold("Any match",   null, false, "any"),
        new ResumeMatchThreshold("Scored only", null, true,  "scored"),
        new ResumeMatchThreshold("Match ≥ 50",   50, true,  "50"),
        new ResumeMatchThreshold("Match ≥ 70",   70, true,  "70"),
        new ResumeMatchThreshold("Match ≥ 85",   85, true,  "85"),
    };

    [ObservableProperty]
    private ResumeMatchThreshold? _selectedResumeMatchThreshold;

    public ObservableCollection<JobAgeFilter> JobAgeFilters { get; } = new()
    {
        new JobAgeFilter("Any age",      null, "any"),
        new JobAgeFilter("Last 1 day",    1,   "1"),
        new JobAgeFilter("Last 3 days",   3,   "3"),
        new JobAgeFilter("Last 7 days",   7,   "7"),
        new JobAgeFilter("Last 90 days", 90,   "90"),
    };

    [ObservableProperty]
    private JobAgeFilter? _selectedJobAge;

    public ObservableCollection<FilterItemViewModel> AvailableLevels { get; } = new();
    public ObservableCollection<FilterItemViewModel> AvailableAreas  { get; } = new();
    public ObservableCollection<FilterItemViewModel> AvailableCities { get; } = new();

    /// <summary>Sort options for the company sidebar. Stored as a record so the combo's
    /// DisplayMemberPath="Label" works directly.</summary>
    public sealed record CompanySortOption(string Label, string Key);

    public ObservableCollection<CompanySortOption> CompanySortModes { get; } = new()
    {
        new CompanySortOption("A → Z",      "name"),
        new CompanySortOption("Most jobs",  "jobs"),
    };

    [ObservableProperty]
    private CompanySortOption? _selectedCompanySort;

    /// <summary>Drop-down entries for quick-loading saved filters.</summary>
    public ObservableCollection<SavedFilterRef> SavedFilterList { get; } = new();

    [ObservableProperty]
    private SavedFilterRef? _selectedSavedFilter;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DiscoverCompaniesCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshJobsCommand))]
    private bool _isBusy;

    private readonly ILevelRepository? _levelsRepo;
    private readonly IAreaRepository? _areasRepo;
    private readonly IConfigRepository? _config;
    private readonly ITechnologyRepository? _techsRepo;
    private bool _settingsLoaded;        // suppresses persistence during initial load

    public MainWindowViewModel(IJobDataService data,
                                IJobRepository? jobsRepo = null,
                                ILevelRepository? levelsRepo = null,
                                IAreaRepository? areasRepo = null,
                                IConfigRepository? config = null,
                                IDiscoveryService? discovery = null,
                                IJobRefresher? refresher = null,
                                Func<SettingsWindow>? settingsWindowFactory = null,
                                Func<CompanyProfileWindow>? profileWindowFactory = null,
                                Func<RefreshWindow>? refreshWindowFactory = null,
                                Func<SavedFiltersWindow>? savedFiltersWindowFactory = null,
                                ISavedFilterRepository? savedFilters = null,
                                Func<ResumeWindow>? resumeWindowFactory = null,
                                Func<ServiceLimitsWindow>? limitsWindowFactory = null,
                                Func<RunsWindow>? runsWindowFactory = null,
                                Func<CoverLetterWindow>? coverLetterWindowFactory = null,
                                ITechnologyRepository? techsRepo = null,
                                Func<ParserReportWindow>? parserReportWindowFactory = null)
    {
        _data = data;
        _jobsRepo = jobsRepo;
        _levelsRepo = levelsRepo;
        _areasRepo = areasRepo;
        _config = config;
        _discovery = discovery;
        _refresher = refresher;
        _settingsWindowFactory = settingsWindowFactory;
        _profileWindowFactory = profileWindowFactory;
        _refreshWindowFactory = refreshWindowFactory;
        _savedFiltersWindowFactory = savedFiltersWindowFactory;
        _savedFilters = savedFilters;
        _resumeWindowFactory = resumeWindowFactory;
        _limitsWindowFactory = limitsWindowFactory;
        _runsWindowFactory = runsWindowFactory;
        _coverLetterWindowFactory = coverLetterWindowFactory;
        _techsRepo = techsRepo;
        _parserReportWindowFactory = parserReportWindowFactory;

        CompaniesView = CollectionViewSource.GetDefaultView(Companies);
        CompaniesView.Filter = FilterCompany;
        // Sentinel "All Jobs" pinned to top regardless of sort. IsAllJobsSentinel descending
        // puts true (sentinel) before false. Secondary sort is applied via ApplyCompanySort.
        CompaniesView.SortDescriptions.Add(new SortDescription(nameof(CompanyViewModel.IsAllJobsSentinel), ListSortDirection.Descending));

        JobsView = CollectionViewSource.GetDefaultView(Jobs);
        // Tab 1 sort: active jobs first, downvoted at the bottom. Within each group, by
        // resume-match score when present (else composite score) descending. Approved jobs are
        // filtered out — they live on the Approved tab.
        JobsView.SortDescriptions.Add(new SortDescription(nameof(JobViewModel.VoteOrder), ListSortDirection.Ascending));
        JobsView.SortDescriptions.Add(new SortDescription(nameof(JobViewModel.SortKey),   ListSortDirection.Descending));
        JobsView.Filter = FilterJobInView;

        // Tab 2: just approved jobs. Sort: not-applied above applied (so the user's "to do"
        // queue is on top, completed at the bottom). Within each group, by score desc.
        var approvedSource = new CollectionViewSource { Source = Jobs };
        ApprovedJobsView = approvedSource.View;
        ApprovedJobsView.SortDescriptions.Add(new SortDescription(nameof(JobViewModel.AppliedSortKey), ListSortDirection.Ascending));
        ApprovedJobsView.SortDescriptions.Add(new SortDescription(nameof(JobViewModel.SortKey),       ListSortDirection.Descending));
        ApprovedJobsView.Filter = o => o is JobViewModel jvm && jvm.IsApproved && jvm.IsActive;

        var savedLevelIds = LoadIdSet("ui_filter_level_ids");
        var savedAreaIds  = LoadIdSet("ui_filter_area_ids");

        if (_levelsRepo is not null)
            foreach (var l in _levelsRepo.GetAll())
                AvailableLevels.Add(new FilterItemViewModel(l.Id, l.Name, savedLevelIds.Contains(l.Id), OnLevelSelectionChanged));
        if (_areasRepo is not null)
            foreach (var a in _areasRepo.GetAll())
                AvailableAreas.Add(new FilterItemViewModel(a.Id, a.Name, savedAreaIds.Contains(a.Id), OnAreaSelectionChanged));

        // Restore non-filter UI prefs
        if (_config is not null)
        {
            JobKeywordFilter    = _config.GetOrDefault("ui_filter_keyword", string.Empty);
            ShowAllCompanies    = _config.GetOrDefault("ui_show_all_companies", "false") == "true";
            ShowRemovedJobs     = _config.GetOrDefault("ui_show_removed_jobs", "false") == "true";
            IncludeAppliedJobs  = _config.GetOrDefault("ui_include_applied", "false") == "true";
            HideAgencyJobs      = _config.GetOrDefault("ui_hide_agency", "false") == "true";
            var savedThresholdKey = _config.GetOrDefault("ui_filter_resume_threshold", "any");
            SelectedResumeMatchThreshold = ResumeMatchThresholds.FirstOrDefault(t => t.Key == savedThresholdKey)
                                            ?? ResumeMatchThresholds.First();
            var savedAgeKey = _config.GetOrDefault("ui_filter_job_age", "any");
            SelectedJobAge = JobAgeFilters.FirstOrDefault(a => a.Key == savedAgeKey)
                              ?? JobAgeFilters.First();
            var savedSortKey = _config.GetOrDefault("ui_company_sort", "name");
            SelectedCompanySort = CompanySortModes.FirstOrDefault(s => s.Key == savedSortKey)
                                   ?? CompanySortModes.First();
        }
        else
        {
            SelectedCompanySort = CompanySortModes.First(); // default: A → Z
        }

        UpdateLevelFilterSummary();
        UpdateAreaFilterSummary();

        LoadFromDataService();
        ReloadSavedFilterList();
        _settingsLoaded = true;
    }

    private HashSet<int> LoadIdSet(string key)
    {
        var set = new HashSet<int>();
        if (_config is null) return set;
        var raw = _config.Get(key);
        if (string.IsNullOrWhiteSpace(raw)) return set;
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(part.Trim(), out var id)) set.Add(id);
        return set;
    }

    private void SaveIdSet(string key, IEnumerable<int> ids)
    {
        if (_config is null) return;
        _config.Set(key, string.Join(",", ids));
    }

    private HashSet<string> LoadNameSet(string key)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_config is null) return set;
        var raw = _config.Get(key);
        if (string.IsNullOrWhiteSpace(raw)) return set;
        // Use '' (unit separator) as the delimiter so city names can contain commas.
        foreach (var part in raw.Split('', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0) set.Add(trimmed);
        }
        return set;
    }

    private void SaveNameSet(string key, IEnumerable<string> names)
    {
        if (_config is null) return;
        _config.Set(key, string.Join('', names));
    }

    private void OnLevelSelectionChanged()
    {
        UpdateLevelFilterSummary();
        RefreshJobsView();
        if (_settingsLoaded)
            SaveIdSet("ui_filter_level_ids", AvailableLevels.Where(x => x.IsSelected).Select(x => x.Id));
    }

    private void OnAreaSelectionChanged()
    {
        UpdateAreaFilterSummary();
        RefreshJobsView();
        if (_settingsLoaded)
            SaveIdSet("ui_filter_area_ids", AvailableAreas.Where(x => x.IsSelected).Select(x => x.Id));
    }

    private void OnCitySelectionChanged()
    {
        UpdateCityFilterSummary();
        RefreshJobsView();
        if (_settingsLoaded)
            SaveNameSet("ui_filter_city_names", AvailableCities.Where(x => x.IsSelected).Select(x => x.Name));
    }

    private void UpdateLevelFilterSummary()
    {
        var picks = AvailableLevels.Where(x => x.IsSelected).Select(x => x.Name).ToList();
        LevelFilterSummary = picks.Count == 0 ? "Any level"
                           : picks.Count <= 2 ? string.Join(", ", picks)
                           : $"{picks.Count} levels";
    }

    private void UpdateAreaFilterSummary()
    {
        var picks = AvailableAreas.Where(x => x.IsSelected).Select(x => x.Name).ToList();
        AreaFilterSummary = picks.Count == 0 ? "Any area"
                          : picks.Count <= 2 ? string.Join(", ", picks)
                          : $"{picks.Count} areas";
    }

    private void UpdateCityFilterSummary()
    {
        var picks = AvailableCities.Where(x => x.IsSelected).Select(x => x.Name).ToList();
        CityFilterSummary = picks.Count == 0 ? "Any city"
                          : picks.Count <= 2 ? string.Join(", ", picks)
                          : $"{picks.Count} cities";
    }

    /// <summary>Rebuild AvailableCities from the user-configured city list in Settings
    /// (config key "cities"). Preserves selection by name across rebuilds.</summary>
    private void RebuildAvailableCities()
    {
        var selectedNames = AvailableCities.Where(x => x.IsSelected).Select(x => x.Name)
                              .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedNames.Count == 0)
            selectedNames = LoadNameSet("ui_filter_city_names");

        var cities = LoadCitiesFromSettings();

        AvailableCities.Clear();
        foreach (var c in cities)
            AvailableCities.Add(new FilterItemViewModel(0, c, selectedNames.Contains(c), OnCitySelectionChanged));

        UpdateCityFilterSummary();
    }

    /// <summary>Read the configured city list from the "cities" config key (JSON array).</summary>
    private List<string> LoadCitiesFromSettings()
    {
        if (_config is null) return new List<string>();
        var raw = _config.GetOrDefault("cities", "[]");
        try
        {
            var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(raw) ?? new();
            return list.Where(s => !string.IsNullOrWhiteSpace(s))
                       .Select(s => s.Trim())
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                       .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>Rebuilds the in-memory views from IJobDataService. Called on startup and after discovery.</summary>
    private void LoadFromDataService()
    {
        var companies = _data.GetCompanies();
        var jobs = _data.GetJobs();
        var companyById = companies.ToDictionary(c => c.Id);

        var levelNameById = _levelsRepo?.GetAll().ToDictionary(l => l.Id, l => l.Name) ?? new Dictionary<int, string>();
        var areaNameById  = _areasRepo?.GetAll().ToDictionary(a => a.Id, a => a.Name)  ?? new Dictionary<int, string>();
        var techNameById  = _techsRepo?.GetAll().ToDictionary(t => t.Id, t => t.Name)  ?? new Dictionary<int, string>();
        var jobTechs      = _techsRepo?.GetAllJobTechnologies()                        ?? new Dictionary<int, List<int>>();

        _allJobs = jobs
            .Where(j => companyById.ContainsKey(j.CompanyId))
            .Select(j =>
            {
                var levelName = j.LevelId.HasValue && levelNameById.TryGetValue(j.LevelId.Value, out var ln) ? ln : null;
                var areaNames = j.AreaIds.Select(id => areaNameById.TryGetValue(id, out var n) ? n : null!)
                                          .Where(n => n is not null);
                var techIdsForJob = jobTechs.TryGetValue(j.Id, out var tids) ? tids : new List<int>();
                var techNames = techIdsForJob
                    .Select(id => techNameById.TryGetValue(id, out var tn) ? tn : null!)
                    .Where(s => s is not null);
                var company = companyById[j.CompanyId];
                return new JobViewModel(j, company.Name, company.City, _data.ScoreJob(j),
                                         levelName, areaNames, OnAppliedToggled, OnInterestChanged,
                                         techNames, techIdsForJob, isAgency: company.IsAgency,
                                         onMarkExpired: OnMarkExpired);
            })
            .ToList();

        var activeJobCounts = jobs
            .Where(j => j.IsActive)
            .GroupBy(j => j.CompanyId)
            .ToDictionary(g => g.Key, g => g.Count());

        // 30-day churn per company, batch-loaded so we don't N+1 in the sidebar render.
        // Absent from the dict => no jobs ≥30 days old yet — sidebar shows "—".
        var churnByCompany = _jobsRepo?.GetChurnRate30dByCompany()
                              ?? new Dictionary<int, Jobnet.Data.Repositories.ChurnStat>();

        var previousSelectionId = SelectedCompany?.Company?.Id;
        var wasAllJobs = SelectedCompany?.IsAllJobsSentinel ?? true;

        Companies.Clear();
        Companies.Add(CompanyViewModel.CreateAllJobsSentinel(jobs.Count(j => j.IsActive)));
        foreach (var c in companies.OrderBy(c => c.Name))
        {
            activeJobCounts.TryGetValue(c.Id, out var count);
            churnByCompany.TryGetValue(c.Id, out var churn);
            Companies.Add(new CompanyViewModel(c, count,
                churnByCompany.ContainsKey(c.Id) ? churn : (Jobnet.Data.Repositories.ChurnStat?)null));
        }

        // Restore selection where possible
        if (wasAllJobs) SelectedCompany = Companies.First();
        else SelectedCompany = Companies.FirstOrDefault(v => v.Company?.Id == previousSelectionId) ?? Companies.First();

        RebuildAvailableCities();
        RefreshStatusBar();
    }

    partial void OnSelectedCompanyChanged(CompanyViewModel? value)
    {
        ReloadJobs();
    }

    partial void OnCompanySearchTextChanged(string value) => CompaniesView.Refresh();

    partial void OnSelectedCompanySortChanged(CompanySortOption? value)
    {
        ApplyCompanySort();
        if (_settingsLoaded && value is not null)
            _config?.Set("ui_company_sort", value.Key);
    }

    /// <summary>Apply the active secondary sort to CompaniesView. The first SortDescription
    /// (IsAllJobsSentinel descending) was added in the ctor and stays so the "All Jobs"
    /// row keeps its pinned position regardless of the user's choice.</summary>
    private void ApplyCompanySort()
    {
        // CompaniesView is always non-null after ctor.
        // Drop any prior secondary sort, then push the new one.
        while (CompaniesView.SortDescriptions.Count > 1)
            CompaniesView.SortDescriptions.RemoveAt(1);

        var key = SelectedCompanySort?.Key ?? "name";
        CompaniesView.SortDescriptions.Add(key switch
        {
            "jobs" => new SortDescription(nameof(CompanyViewModel.ActiveJobCount), ListSortDirection.Descending),
            _      => new SortDescription(nameof(CompanyViewModel.Name),           ListSortDirection.Ascending),
        });
        CompaniesView.Refresh();
    }

    partial void OnShowAllCompaniesChanged(bool value)
    {
        CompaniesView.Refresh();
        if (_settingsLoaded) _config?.Set("ui_show_all_companies", value ? "true" : "false");
    }

    partial void OnShowRemovedJobsChanged(bool value)
    {
        ReloadJobs();
        if (_settingsLoaded) _config?.Set("ui_show_removed_jobs", value ? "true" : "false");
    }

    partial void OnIncludeAppliedJobsChanged(bool value)
    {
        RefreshJobsView();
        if (_settingsLoaded) _config?.Set("ui_include_applied", value ? "true" : "false");
    }

    partial void OnHideAgencyJobsChanged(bool value)
    {
        RefreshJobsView();
        if (_settingsLoaded) _config?.Set("ui_hide_agency", value ? "true" : "false");
    }

    partial void OnJobKeywordFilterChanged(string value)
    {
        RefreshJobsView();
        if (_settingsLoaded) _config?.Set("ui_filter_keyword", value ?? string.Empty);
    }

    partial void OnSelectedJobAgeChanged(JobAgeFilter? value)
    {
        RefreshJobsView();
        if (_settingsLoaded) _config?.Set("ui_filter_job_age", value?.Key ?? "any");
    }

    partial void OnSelectedResumeMatchThresholdChanged(ResumeMatchThreshold? value)
    {
        RefreshJobsView();
        if (_settingsLoaded) _config?.Set("ui_filter_resume_threshold", value?.Key ?? "any");
    }

    private void RefreshJobsView()
    {
        JobsView.Refresh();
        ApprovedJobsView.Refresh();
        UpdateFilteredCount();
        UpdateTabCounts();
    }

    private void UpdateFilteredCount()
    {
        var visible = JobsView.Cast<object>().Count();
        var total = Jobs.Count;
        FilteredJobsCountText = visible == total
            ? $"{visible} job{(visible == 1 ? "" : "s")}"
            : $"{visible} of {total} jobs shown";
    }

    private void UpdateTabCounts()
    {
        AllJobsTabCount      = JobsView.Cast<object>().Count();
        ApprovedJobsTabCount = ApprovedJobsView.Cast<object>().Count();
    }

    private void ReloadJobs()
    {
        Jobs.Clear();
        if (SelectedCompany is null) return;

        IEnumerable<JobViewModel> source = _allJobs;
        if (!SelectedCompany.IsAllJobsSentinel)
            source = source.Where(j => j.Job.CompanyId == SelectedCompany.Company!.Id);
        if (!ShowRemovedJobs)
            source = source.Where(j => j.IsActive);

        foreach (var j in source.OrderBy(j => j.VoteOrder).ThenByDescending(j => j.SortKey))
            Jobs.Add(j);

        JobsPaneHeader = SelectedCompany.IsAllJobsSentinel ? "All Jobs" : $"Jobs for: {SelectedCompany.Name}";
        UpdateFilteredCount();
        RefreshStatusBar();
    }

    private bool FilterCompany(object obj)
    {
        if (obj is not CompanyViewModel vm) return false;
        if (vm.IsAllJobsSentinel) return true;

        // Default view: hide companies with 0 active jobs. Toggle via ShowAllCompanies.
        if (!ShowAllCompanies && vm.ActiveJobCount == 0) return false;

        if (string.IsNullOrWhiteSpace(CompanySearchText)) return true;
        return vm.Name.Contains(CompanySearchText, StringComparison.OrdinalIgnoreCase);
    }

    private bool FilterJobInView(object obj)
    {
        if (obj is not JobViewModel jvm) return false;

        // Approved jobs live on the Approved-jobs tab — keep them off this one.
        if (jvm.IsApproved) return false;

        // Hide applied jobs unless the user has explicitly opted in.
        if (jvm.IsApplied && !IncludeAppliedJobs) return false;

        var selectedLevels = AvailableLevels.Where(x => x.IsSelected).Select(x => x.Id).ToList();
        if (selectedLevels.Count > 0 && (jvm.Job.LevelId is null || !selectedLevels.Contains(jvm.Job.LevelId.Value)))
            return false;

        var selectedAreas = AvailableAreas.Where(x => x.IsSelected).Select(x => x.Id).ToList();
        if (selectedAreas.Count > 0 && !jvm.Job.AreaIds.Any(id => selectedAreas.Contains(id)))
            return false;

        var selectedCities = AvailableCities.Where(x => x.IsSelected).Select(x => x.Name).ToList();
        if (selectedCities.Count > 0
            && !selectedCities.Contains(jvm.NormalizedCity, StringComparer.OrdinalIgnoreCase)
            && !Services.Location.LocationMatcher.IsRemoteAnywhereInCanada(jvm.Job.Location))
            return false;

        if (HideAgencyJobs && jvm.IsAgency) return false;

        var threshold = SelectedResumeMatchThreshold;
        if (threshold is not null)
        {
            if (threshold.RequireScored && jvm.Job.ResumeMatchScore is null) return false;
            if (threshold.MinScore is int min && (jvm.Job.ResumeMatchScore ?? -1) < min) return false;
        }

        if (SelectedJobAge?.Days is int maxDays)
        {
            var cutoff = DateTime.UtcNow.AddDays(-maxDays);
            if (jvm.Job.DateFirstSeen < cutoff) return false;
        }

        if (!string.IsNullOrWhiteSpace(JobKeywordFilter))
        {
            var needle = JobKeywordFilter.Trim();
            var inTitle   = jvm.Job.Title?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
            var inCompany = jvm.CompanyName?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
            var inDesc    = jvm.Job.DescriptionSnippet?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
            var inLoc     = jvm.Job.Location?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
            var inCity    = jvm.CompanyCity?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
            if (!(inTitle || inCompany || inDesc || inLoc || inCity)) return false;
        }
        return true;
    }

    private void RefreshStatusBar()
    {
        var totalActive = _allJobs.Count(j => j.IsActive);
        var companyCount = Companies.Count - 1; // minus the All Jobs sentinel
        StatusBarText = $"{companyCount} companies · {totalActive} active jobs · Last refresh: {DateTime.Now:yyyy-MM-dd HH:mm}";
    }

    private bool CanRunBackground() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRunBackground))]
    private async Task DiscoverCompaniesAsync()
    {
        if (_discovery is null)
        {
            StatusBarText = "Discovery service is not available.";
            return;
        }

        IsBusy = true;
        StatusBarText = "Discovering companies — running search queries...";
        try
        {
            var report = await Task.Run(() => _discovery.RunAsync()).ConfigureAwait(true);
            LoadFromDataService();

            if (report.Errors.Count > 0)
            {
                StatusBarText = $"Discovery finished with errors. Added {report.CompaniesAdded}, skipped {report.CompaniesSkippedExisting} existing. First error: {report.Errors[0]}";
            }
            else
            {
                StatusBarText = $"Discovery complete. {report.QueriesIssued} queries, " +
                                $"{report.ResultsExamined} results examined ({report.ResultsSkippedFiltered} filtered), " +
                                $"{report.CompaniesAdded} companies added, {report.CompaniesSkippedExisting} already in DB.";
            }
        }
        catch (InvalidOperationException ex)
        {
            StatusBarText = $"Discovery failed: {ex.Message}";
        }
        catch (Exception ex)
        {
            StatusBarText = $"Discovery failed: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunBackground))]
    private async Task RefreshJobsAsync()
    {
        if (_refresher is null) { StatusBarText = "Refresher service not available."; return; }
        IsBusy = true;
        StatusBarText = "Refreshing jobs from detected ATS providers...";
        try
        {
            var r = await Task.Run(() => _refresher.RefreshAllAsync()).ConfigureAwait(true);
            LoadFromDataService();
            if (r.Errors.Count > 0)
                StatusBarText = $"Refresh done with errors. {r.CompaniesProcessed} processed, {r.JobsAdded} added, {r.JobsUpdated} updated, {r.JobsRemoved} removed. First error: {r.Errors[0]}";
            else
                StatusBarText = $"Refresh complete. {r.CompaniesProcessed} processed, {r.CompaniesSkippedNoAts} skipped (no ATS), {r.JobsAdded} added, {r.JobsUpdated} updated, {r.JobsRemoved} removed.";
        }
        catch (Exception ex)
        {
            StatusBarText = $"Refresh failed: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        if (_settingsWindowFactory is null) return;
        var window = _settingsWindowFactory();
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();
    }

    private bool _refreshVmSubscribed;

    [RelayCommand]
    private void OpenRefresh()
    {
        if (_refreshWindowFactory is null) return;
        var window = _refreshWindowFactory();
        // The RefreshViewModel is a singleton — only subscribe Completed once, otherwise
        // every reopen would add another LoadFromDataService handler.
        if (!_refreshVmSubscribed && window.DataContext is RefreshViewModel rvm)
        {
            rvm.Completed += LoadFromDataService;
            _refreshVmSubscribed = true;
        }
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();
    }

    [RelayCommand]
    private void OpenResume()
    {
        if (_resumeWindowFactory is null) return;
        var window = _resumeWindowFactory();
        if (window.DataContext is ResumeViewModel rvm)
            rvm.Completed += LoadFromDataService;
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();
    }

    [RelayCommand]
    private void OpenServiceLimits()
    {
        if (_limitsWindowFactory is null) return;
        var window = _limitsWindowFactory();
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();
    }

    [RelayCommand]
    private void OpenParserReport()
    {
        if (_parserReportWindowFactory is null) return;
        var window = _parserReportWindowFactory();
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();
    }

    [RelayCommand]
    private void OpenRuns()
    {
        if (_runsWindowFactory is null) return;
        var window = _runsWindowFactory();
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();
    }


    [RelayCommand]
    private void OpenCoverLetter(JobViewModel? job)
    {
        if (job is null || _coverLetterWindowFactory is null) return;
        var window = _coverLetterWindowFactory();
        if (window.DataContext is CoverLetterViewModel vm)
            vm.Load(job.Job, job.CompanyName);
        window.Owner = Application.Current.MainWindow;
        window.Show();   // non-modal — user may switch back to the job list
    }

    [RelayCommand]
    private void ClearJobFilters()
    {
        JobKeywordFilter = string.Empty;
        foreach (var f in AvailableLevels) f.IsSelected = false;
        foreach (var f in AvailableAreas)  f.IsSelected = false;
        foreach (var f in AvailableCities) f.IsSelected = false;
        SelectedResumeMatchThreshold = ResumeMatchThresholds.First();
        SelectedJobAge = JobAgeFilters.First();
    }

    // ── Saved filter presets ────────────────────────────────────────────────

    private FilterStateSnapshot CaptureFilterSnapshot() => new()
    {
        Keyword          = JobKeywordFilter ?? "",
        LevelIds         = AvailableLevels.Where(x => x.IsSelected).Select(x => x.Id).ToList(),
        AreaIds          = AvailableAreas.Where(x => x.IsSelected).Select(x => x.Id).ToList(),
        CityNames        = AvailableCities.Where(x => x.IsSelected).Select(x => x.Name).ToList(),
        ShowAllCompanies = ShowAllCompanies,
        ShowRemovedJobs  = ShowRemovedJobs,
    };

    private void ApplyFilterSnapshot(FilterStateSnapshot s)
    {
        // Apply with the persisted-save guard temporarily off, then flush once at the end,
        // so we don't write the snapshot fields back to config one-by-one as they change.
        var prev = _settingsLoaded;
        _settingsLoaded = false;
        try
        {
            JobKeywordFilter = s.Keyword ?? "";
            var levelSet = new HashSet<int>(s.LevelIds);
            foreach (var f in AvailableLevels) f.IsSelected = levelSet.Contains(f.Id);
            var areaSet = new HashSet<int>(s.AreaIds);
            foreach (var f in AvailableAreas)  f.IsSelected = areaSet.Contains(f.Id);
            var citySet = new HashSet<string>(s.CityNames, StringComparer.OrdinalIgnoreCase);
            foreach (var f in AvailableCities) f.IsSelected = citySet.Contains(f.Name);
            ShowAllCompanies = s.ShowAllCompanies;
            ShowRemovedJobs  = s.ShowRemovedJobs;
        }
        finally
        {
            _settingsLoaded = prev;
        }

        // One persistence flush + one view refresh now that everything is set.
        if (_settingsLoaded && _config is not null)
        {
            _config.Set("ui_filter_keyword", JobKeywordFilter);
            _config.Set("ui_show_all_companies", ShowAllCompanies ? "true" : "false");
            _config.Set("ui_show_removed_jobs",  ShowRemovedJobs  ? "true" : "false");
            SaveIdSet("ui_filter_level_ids",  AvailableLevels.Where(x => x.IsSelected).Select(x => x.Id));
            SaveIdSet("ui_filter_area_ids",   AvailableAreas.Where(x => x.IsSelected).Select(x => x.Id));
            SaveNameSet("ui_filter_city_names", AvailableCities.Where(x => x.IsSelected).Select(x => x.Name));
        }

        UpdateLevelFilterSummary();
        UpdateAreaFilterSummary();
        UpdateCityFilterSummary();
        RefreshJobsView();
    }

    [RelayCommand]
    private void SaveCurrentFilter()
    {
        if (_savedFilters is null) return;
        var name = Views.TextPromptWindow.Ask(
            Application.Current.MainWindow,
            title: "Save filter",
            prompt: "Name this filter:",
            initialValue: SuggestFilterName());
        if (string.IsNullOrWhiteSpace(name)) return;

        var snap = CaptureFilterSnapshot();
        var json = System.Text.Json.JsonSerializer.Serialize(snap);
        _savedFilters.Upsert(name, json);
        StatusBarText = $"Saved filter '{name}'.";
        ReloadSavedFilterList();
    }

    /// <summary>Repopulate the saved-filters dropdown from the repo. Preserves no selection (the
    /// dropdown is for quick-load, not state).</summary>
    private void ReloadSavedFilterList()
    {
        SavedFilterList.Clear();
        if (_savedFilters is null) return;
        foreach (var f in _savedFilters.GetAll())
            SavedFilterList.Add(new SavedFilterRef { Id = f.Id, Name = f.Name, Payload = f.Payload });
    }

    partial void OnSelectedSavedFilterChanged(SavedFilterRef? value)
    {
        if (value is null) return;
        try
        {
            var snap = System.Text.Json.JsonSerializer.Deserialize<FilterStateSnapshot>(value.Payload);
            if (snap is not null)
            {
                ApplyFilterSnapshot(snap);
                _savedFilters?.MarkUsed(value.Id);
                StatusBarText = $"Loaded filter '{value.Name}'.";
            }
        }
        catch (Exception ex) { StatusBarText = $"Failed to load filter: {ex.Message}"; }
        // Reset the dropdown to the placeholder so selecting the same item again still re-applies.
        // Defer with a beginInvoke so we don't recurse into the change handler synchronously.
        Application.Current?.Dispatcher.BeginInvoke(new Action(() => SelectedSavedFilter = null));
    }

    private string SuggestFilterName()
    {
        var parts = new List<string>();
        var levels = AvailableLevels.Where(x => x.IsSelected).Select(x => x.Name).ToList();
        if (levels.Count > 0) parts.Add(string.Join("/", levels));
        var areas = AvailableAreas.Where(x => x.IsSelected).Select(x => x.Name).ToList();
        if (areas.Count > 0) parts.Add(string.Join("/", areas));
        var cities = AvailableCities.Where(x => x.IsSelected).Select(x => x.Name).ToList();
        if (cities.Count > 0) parts.Add(string.Join("/", cities));
        if (!string.IsNullOrWhiteSpace(JobKeywordFilter)) parts.Add($"'{JobKeywordFilter}'");
        return parts.Count == 0 ? "Filter" : string.Join(" — ", parts);
    }

    [RelayCommand]
    private void OpenSavedFilters()
    {
        if (_savedFiltersWindowFactory is null) return;
        var window = _savedFiltersWindowFactory();
        if (window.DataContext is SavedFiltersViewModel vm)
        {
            vm.Reload();
            vm.OnLoadRequested = f =>
            {
                try
                {
                    var snap = System.Text.Json.JsonSerializer.Deserialize<FilterStateSnapshot>(f.Payload);
                    if (snap is not null)
                    {
                        ApplyFilterSnapshot(snap);
                        StatusBarText = $"Loaded filter '{f.Name}'.";
                    }
                }
                catch (Exception ex) { StatusBarText = $"Failed to load filter: {ex.Message}"; }
                window.Close();
            };
        }
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();
        ReloadSavedFilterList();
    }

    public void OpenCompanyProfile(int companyId)
    {
        if (_profileWindowFactory is null) return;
        var window = _profileWindowFactory();
        if (window.DataContext is CompanyProfileViewModel vm)
            vm.Load(companyId);
        window.Owner = Application.Current.MainWindow;
        window.Show();
    }

    // ---- Job context-menu commands ----

    [RelayCommand]
    private void MarkJobInteresting(JobViewModel? job) => SetJobInterest(job, Models.InterestLevel.Approved);

    [RelayCommand]
    private void MarkJobNotInteresting(JobViewModel? job) => SetJobInterest(job, Models.InterestLevel.NotInteresting);

    [RelayCommand]
    private void ClearJobInterest(JobViewModel? job) => SetJobInterest(job, Models.InterestLevel.Neutral);

    [RelayCommand]
    private void CopyJobUrl(JobViewModel? job)
    {
        if (job?.Job.Url is null) return;
        try { Clipboard.SetText(job.Job.Url); StatusBarText = $"Copied: {job.Job.Url}"; }
        catch (Exception ex) { StatusBarText = $"Copy failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void CopyJobId(JobViewModel? job)
    {
        if (job is null) return;
        try
        {
            Clipboard.SetText(job.Job.Id.ToString());
            StatusBarText = $"Copied job id: {job.Job.Id}";
            _ = job.FlashCopiedAsync();   // fire-and-forget visual confirmation on the card itself
        }
        catch (Exception ex) { StatusBarText = $"Copy failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void OpenJobInBrowser(JobViewModel? job)
    {
        if (string.IsNullOrWhiteSpace(job?.Job.Url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = job.Job.Url, UseShellExecute = true
            });
        }
        catch (Exception ex) { StatusBarText = $"Could not open: {ex.Message}"; }
    }

    /// <summary>Wired into every JobViewModel; persists the Applied state to the DB and updates
    /// the in-memory Job model so the binding/dim styles stay in sync. Also re-runs the
    /// JobsView filter so applied jobs disappear from view (unless the user has Include Applied on).</summary>
    private void OnAppliedToggled(JobViewModel jvm, bool isApplied)
    {
        if (_jobsRepo is null) return;
        try
        {
            _jobsRepo.SetApplied(jvm.Job.Id, isApplied);
            jvm.Job.DateApplied = isApplied ? DateTime.UtcNow : null;
            StatusBarText = isApplied
                ? $"Marked '{jvm.Job.Title}' as applied."
                : $"Unmarked '{jvm.Job.Title}' applied.";
            RefreshJobsView();
        }
        catch (Exception ex) { StatusBarText = $"Apply toggle failed: {ex.Message}"; }
    }

    /// <summary>Wired into every JobViewModel; "Expired" on an approved card unapproves the job
    /// AND marks it removed (is_active=0, date_removed=now). The ApprovedJobsView filter is
    /// IsApproved &amp;&amp; IsActive, so flipping either alone would still leave the row visible
    /// after refresh — we flip both.</summary>
    private void OnMarkExpired(JobViewModel jvm)
    {
        if (_jobsRepo is null) return;
        try
        {
            var now = DateTime.UtcNow;
            _jobsRepo.MarkRemoved(jvm.Job.Id, now);
            _jobsRepo.SetInterestLevel(jvm.Job.Id, Models.InterestLevel.Neutral);
            jvm.Job.IsActive = false;
            jvm.Job.DateRemoved = now;
            jvm.Job.InterestLevel = Models.InterestLevel.Neutral;
            StatusBarText = $"Marked '{jvm.Job.Title}' expired.";
            RefreshJobsView();
        }
        catch (Exception ex) { StatusBarText = $"Expire failed: {ex.Message}"; }
    }

    /// <summary>Wired into every JobViewModel; persists the vote (Interesting/NotInteresting/Neutral)
    /// to the DB and re-runs the sort so upvotes float to the top and downvotes sink to the bottom.</summary>
    private void OnInterestChanged(JobViewModel jvm, Models.InterestLevel level)
    {
        if (_jobsRepo is null) return;
        try
        {
            _jobsRepo.SetInterestLevel(jvm.Job.Id, level);
            StatusBarText = level switch
            {
                Models.InterestLevel.Approved    => $"Upvoted '{jvm.Job.Title}'.",
                Models.InterestLevel.NotInteresting => $"Downvoted '{jvm.Job.Title}'.",
                _                                   => $"Cleared vote on '{jvm.Job.Title}'.",
            };
            RefreshJobsView();
        }
        catch (Exception ex) { StatusBarText = $"Vote failed: {ex.Message}"; }
    }

    private void SetJobInterest(JobViewModel? job, Models.InterestLevel level)
    {
        if (job is null || _jobsRepo is null) return;
        _jobsRepo.SetInterestLevel(job.Job.Id, level);
        job.Job.InterestLevel = level;
        // Recreate the row so the glyph/binding updates (JobViewModel doesn't observe Job.InterestLevel).
        var idx = Jobs.IndexOf(job);
        if (idx >= 0)
        {
            Jobs.RemoveAt(idx);
            var refreshed = new JobViewModel(job.Job, job.CompanyName, job.CompanyCity, job.CompositeScore,
                                              levelName: job.LevelName == "Unclassified" ? null : job.LevelName,
                                              areaNames: job.AreasDisplay == "—" ? null : job.AreasDisplay.Split(", ", StringSplitOptions.RemoveEmptyEntries),
                                              onAppliedToggled: OnAppliedToggled,
                                              onInterestChanged: OnInterestChanged,
                                              onMarkExpired: OnMarkExpired);
            Jobs.Insert(idx, refreshed);
        }
        // Keep _allJobs in sync too
        var allIdx = _allJobs.IndexOf(job);
        if (allIdx >= 0) _allJobs[allIdx] = new JobViewModel(job.Job, job.CompanyName, job.CompanyCity, job.CompositeScore,
                                              levelName: job.LevelName == "Unclassified" ? null : job.LevelName,
                                              areaNames: job.AreasDisplay == "—" ? null : job.AreasDisplay.Split(", ", StringSplitOptions.RemoveEmptyEntries),
                                              onAppliedToggled: OnAppliedToggled,
                                              onInterestChanged: OnInterestChanged,
                                              onMarkExpired: OnMarkExpired);
        StatusBarText = $"Marked '{job.Job.Title}' as {level.ToString().ToLowerInvariant()}";
    }
}
