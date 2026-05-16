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
    private List<JobViewModel> _allJobs = new();

    public ObservableCollection<CompanyViewModel> Companies { get; } = new();
    public ICollectionView CompaniesView { get; }

    public ObservableCollection<JobViewModel> Jobs { get; } = new();
    public ICollectionView JobsView { get; }

    [ObservableProperty]
    private CompanyViewModel? _selectedCompany;

    [ObservableProperty]
    private string _companySearchText = string.Empty;

    [ObservableProperty]
    private bool _showAllCompanies;          // default: only companies with active jobs

    [ObservableProperty]
    private bool _showRemovedJobs;

    [ObservableProperty]
    private string _statusBarText = string.Empty;

    [ObservableProperty]
    private string _jobsPaneHeader = "All Jobs";

    // Job filters (right pane)
    [ObservableProperty]
    private string _jobKeywordFilter = string.Empty;

    [ObservableProperty]
    private Level? _levelFilter;     // null = any

    [ObservableProperty]
    private Area? _areaFilter;       // null = any

    public ObservableCollection<Level> AvailableLevels { get; } = new();
    public ObservableCollection<Area>  AvailableAreas  { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DiscoverCompaniesCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshJobsCommand))]
    private bool _isBusy;

    private readonly ILevelRepository? _levelsRepo;
    private readonly IAreaRepository? _areasRepo;

    public MainWindowViewModel(IJobDataService data,
                                IJobRepository? jobsRepo = null,
                                ILevelRepository? levelsRepo = null,
                                IAreaRepository? areasRepo = null,
                                IDiscoveryService? discovery = null,
                                IJobRefresher? refresher = null,
                                Func<SettingsWindow>? settingsWindowFactory = null,
                                Func<CompanyProfileWindow>? profileWindowFactory = null)
    {
        _data = data;
        _jobsRepo = jobsRepo;
        _levelsRepo = levelsRepo;
        _areasRepo = areasRepo;
        _discovery = discovery;
        _refresher = refresher;
        _settingsWindowFactory = settingsWindowFactory;
        _profileWindowFactory = profileWindowFactory;

        CompaniesView = CollectionViewSource.GetDefaultView(Companies);
        CompaniesView.Filter = FilterCompany;

        JobsView = CollectionViewSource.GetDefaultView(Jobs);
        JobsView.SortDescriptions.Add(new SortDescription(nameof(JobViewModel.CompositeScore), ListSortDirection.Descending));
        JobsView.Filter = FilterJobInView;

        if (_levelsRepo is not null)
            foreach (var l in _levelsRepo.GetAll()) AvailableLevels.Add(l);
        if (_areasRepo is not null)
            foreach (var a in _areasRepo.GetAll()) AvailableAreas.Add(a);

        LoadFromDataService();
    }

    /// <summary>Rebuilds the in-memory views from IJobDataService. Called on startup and after discovery.</summary>
    private void LoadFromDataService()
    {
        var companies = _data.GetCompanies();
        var jobs = _data.GetJobs();
        var companyById = companies.ToDictionary(c => c.Id);

        _allJobs = jobs
            .Where(j => companyById.ContainsKey(j.CompanyId))
            .Select(j => new JobViewModel(j, companyById[j.CompanyId].Name, _data.ScoreJob(j)))
            .ToList();

        var activeJobCounts = jobs
            .Where(j => j.IsActive)
            .GroupBy(j => j.CompanyId)
            .ToDictionary(g => g.Key, g => g.Count());

        var previousSelectionId = SelectedCompany?.Company?.Id;
        var wasAllJobs = SelectedCompany?.IsAllJobsSentinel ?? true;

        Companies.Clear();
        Companies.Add(CompanyViewModel.CreateAllJobsSentinel(jobs.Count(j => j.IsActive)));
        foreach (var c in companies.OrderBy(c => c.Name))
        {
            activeJobCounts.TryGetValue(c.Id, out var count);
            Companies.Add(new CompanyViewModel(c, count));
        }

        // Restore selection where possible
        if (wasAllJobs) SelectedCompany = Companies.First();
        else SelectedCompany = Companies.FirstOrDefault(v => v.Company?.Id == previousSelectionId) ?? Companies.First();

        RefreshStatusBar();
    }

    partial void OnSelectedCompanyChanged(CompanyViewModel? value)
    {
        ReloadJobs();
    }

    partial void OnCompanySearchTextChanged(string value) => CompaniesView.Refresh();
    partial void OnShowAllCompaniesChanged(bool value) => CompaniesView.Refresh();

    partial void OnShowRemovedJobsChanged(bool value) => ReloadJobs();
    partial void OnJobKeywordFilterChanged(string value) => JobsView.Refresh();
    partial void OnLevelFilterChanged(Level? value) => JobsView.Refresh();
    partial void OnAreaFilterChanged(Area? value) => JobsView.Refresh();

    private void ReloadJobs()
    {
        Jobs.Clear();
        if (SelectedCompany is null) return;

        IEnumerable<JobViewModel> source = _allJobs;
        if (!SelectedCompany.IsAllJobsSentinel)
            source = source.Where(j => j.Job.CompanyId == SelectedCompany.Company!.Id);
        if (!ShowRemovedJobs)
            source = source.Where(j => j.IsActive);

        foreach (var j in source.OrderByDescending(j => j.CompositeScore))
            Jobs.Add(j);

        JobsPaneHeader = SelectedCompany.IsAllJobsSentinel ? "All Jobs" : $"Jobs for: {SelectedCompany.Name}";
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

        if (LevelFilter is not null && jvm.Job.LevelId != LevelFilter.Id) return false;
        if (AreaFilter is not null && !jvm.Job.AreaIds.Contains(AreaFilter.Id)) return false;

        if (!string.IsNullOrWhiteSpace(JobKeywordFilter))
        {
            var needle = JobKeywordFilter.Trim();
            var inTitle   = jvm.Job.Title?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
            var inCompany = jvm.CompanyName?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
            var inDesc    = jvm.Job.DescriptionSnippet?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
            var inLoc     = jvm.Job.Location?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
            if (!(inTitle || inCompany || inDesc || inLoc)) return false;
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

    [RelayCommand]
    private void ClearJobFilters()
    {
        JobKeywordFilter = string.Empty;
        LevelFilter = null;
        AreaFilter = null;
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
    private void MarkJobInteresting(JobViewModel? job) => SetJobInterest(job, Models.InterestLevel.Interesting);

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
            var refreshed = new JobViewModel(job.Job, job.CompanyName, job.CompositeScore);
            Jobs.Insert(idx, refreshed);
        }
        // Keep _allJobs in sync too
        var allIdx = _allJobs.IndexOf(job);
        if (allIdx >= 0) _allJobs[allIdx] = new JobViewModel(job.Job, job.CompanyName, job.CompositeScore);
        StatusBarText = $"Marked '{job.Job.Title}' as {level.ToString().ToLowerInvariant()}";
    }
}
