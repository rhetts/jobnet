using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Jobnet.Services.Profiling;

namespace Jobnet.ViewModels;

public partial class CompanyProfileViewModel : ObservableObject
{
    private readonly ICompanyRepository _companies;
    private readonly ICompanyProfiler _profiler;

    [ObservableProperty] private Company? _company;
    [ObservableProperty] private CompanyProfile? _profile;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegenerateProfileCommand))]
    private bool _isWorking;
    [ObservableProperty] private string _statusText = string.Empty;

    public IReadOnlyList<string> Products    => Profile?.Products    ?? Array.Empty<string>();
    public IReadOnlyList<string> Industries  => Profile?.Industries  ?? Array.Empty<string>();
    public IReadOnlyList<string> TechSignals => Profile?.TechSignals ?? Array.Empty<string>();

    public string HeaderTitle => Company?.Name ?? "(no company)";
    public string SubHeader  => Company is null ? "" : $"{Company.Domain}";

    public string AtsLine =>
        Company is null ? ""
        : string.IsNullOrEmpty(Company.AtsType) ? "ATS: (not detected)"
        : $"ATS: {Company.AtsType}{(string.IsNullOrEmpty(Company.AtsSlug) ? "" : ":" + Company.AtsSlug)}";

    public string CareersLine =>
        Company is null || string.IsNullOrWhiteSpace(Company.CareersUrl)
            ? "Careers URL: (none)" : "Careers URL: " + Company.CareersUrl;

    public string SummaryText =>
        Profile?.Summary ?? "No profile yet. Click \"Generate Profile\" to create one with Claude Haiku.";

    public string GeneratedLine =>
        Profile?.GeneratedAt is null ? "" : $"Generated {Profile.GeneratedAt.Value:yyyy-MM-dd HH:mm} UTC · {Profile.Model}";

    public CompanyProfileViewModel(ICompanyRepository companies, ICompanyProfiler profiler)
    {
        _companies = companies;
        _profiler = profiler;
    }

    public void Load(int companyId)
    {
        Company = _companies.GetById(companyId);
        Profile = Company is null ? null : _companies.GetProfile(Company.Id);
        Notify();
        StatusText = "";
    }

    private bool CanRegenerate() => !IsWorking && Company is not null;

    [RelayCommand(CanExecute = nameof(CanRegenerate))]
    private async Task RegenerateProfileAsync()
    {
        if (Company is null) return;
        IsWorking = true;
        StatusText = "Fetching homepage and calling Claude Haiku...";
        try
        {
            var result = await Task.Run(() => _profiler.GenerateAndPersistAsync(Company)).ConfigureAwait(true);
            if (result.Success)
            {
                Profile = result.Profile;
                StatusText = $"Generated from {result.SourceUrl}";
            }
            else
            {
                StatusText = $"Failed: {result.Error}";
            }
            Notify();
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private void OpenWebsite()
    {
        if (Company is null) return;
        var url = Company.WebsiteUrl ?? $"https://{Company.Domain}";
        OpenInBrowser(url);
    }

    [RelayCommand]
    private void OpenCareers()
    {
        if (Company?.CareersUrl is null) return;
        OpenInBrowser(Company.CareersUrl);
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch { }
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(Products));
        OnPropertyChanged(nameof(Industries));
        OnPropertyChanged(nameof(TechSignals));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(SubHeader));
        OnPropertyChanged(nameof(AtsLine));
        OnPropertyChanged(nameof(CareersLine));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(GeneratedLine));
    }
}
