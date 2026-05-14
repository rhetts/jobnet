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
using Jobnet.Services;
using Jobnet.Services.Discovery;
using Jobnet.Views;

namespace Jobnet.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly IJobDataService _data;
    private readonly IDiscoveryService? _discovery;
    private readonly Func<SettingsWindow>? _settingsWindowFactory;
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
    private bool _showRemovedJobs;

    [ObservableProperty]
    private string _statusBarText = string.Empty;

    [ObservableProperty]
    private string _jobsPaneHeader = "All Jobs";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DiscoverCompaniesCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshJobsCommand))]
    private bool _isBusy;

    public MainWindowViewModel(IJobDataService data,
                                IDiscoveryService? discovery = null,
                                Func<SettingsWindow>? settingsWindowFactory = null)
    {
        _data = data;
        _discovery = discovery;
        _settingsWindowFactory = settingsWindowFactory;

        CompaniesView = CollectionViewSource.GetDefaultView(Companies);
        CompaniesView.Filter = FilterCompany;

        JobsView = CollectionViewSource.GetDefaultView(Jobs);
        JobsView.SortDescriptions.Add(new SortDescription(nameof(JobViewModel.CompositeScore), ListSortDirection.Descending));

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

    partial void OnCompanySearchTextChanged(string value)
    {
        CompaniesView.Refresh();
    }

    partial void OnShowRemovedJobsChanged(bool value)
    {
        ReloadJobs();
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

        foreach (var j in source.OrderByDescending(j => j.CompositeScore))
            Jobs.Add(j);

        JobsPaneHeader = SelectedCompany.IsAllJobsSentinel ? "All Jobs" : $"Jobs for: {SelectedCompany.Name}";
        RefreshStatusBar();
    }

    private bool FilterCompany(object obj)
    {
        if (obj is not CompanyViewModel vm) return false;
        if (vm.IsAllJobsSentinel) return true;
        if (string.IsNullOrWhiteSpace(CompanySearchText)) return true;
        return vm.Name.Contains(CompanySearchText, StringComparison.OrdinalIgnoreCase);
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
    private void RefreshJobs()
    {
        StatusBarText = "Refresh Jobs — not yet wired up (Phase 8).";
    }

    [RelayCommand]
    private void OpenSettings()
    {
        if (_settingsWindowFactory is null) return;
        var window = _settingsWindowFactory();
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();
    }
}
