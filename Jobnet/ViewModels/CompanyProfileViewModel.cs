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
    private readonly ICompanyDiscoveryRepository _discoveries;
    private readonly IJobRepository _jobs;

    [ObservableProperty] private Company? _company;
    [ObservableProperty] private CompanyProfile? _profile;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegenerateProfileCommand))]
    private bool _isWorking;
    [ObservableProperty] private string _statusText = string.Empty;

    /// <summary>All recorded discovery sightings for the loaded company, earliest first.
    /// Populated on Load(). May be empty if migration 039 hasn't run yet on legacy data.</summary>
    public IReadOnlyList<CompanyDiscovery> Discoveries { get; private set; } = Array.Empty<CompanyDiscovery>();

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

    /// <summary>Primary line for the DISCOVERY section. Shows the earliest sighting we have
    /// recorded — typically the source that originally surfaced this company. If no rows
    /// exist (pre-migration legacy data that hasn't been backfilled yet) we say so explicitly
    /// rather than silently omit the section.</summary>
    public string DiscoveryLine
    {
        get
        {
            if (Discoveries.Count == 0) return "(not recorded)";
            var first = Discoveries[Discoveries.Count - 1];   // GetByCompany returns DESC; last is earliest
            var when = first.DiscoveredAt.ToLocalTime().ToString("yyyy-MM-dd");
            return $"{first.SourceType} — {first.SourceName} (on {when})";
        }
    }

    /// <summary>Additional sightings beyond the first one (same company discovered multiple
    /// times via different sources). Empty for the common case.</summary>
    public IReadOnlyList<string> AdditionalDiscoveryLines
    {
        get
        {
            if (Discoveries.Count <= 1) return Array.Empty<string>();
            var list = new List<string>(Discoveries.Count - 1);
            // Skip the last entry (already shown as the primary) and emit the rest, newest first.
            for (var i = 0; i < Discoveries.Count - 1; i++)
            {
                var d = Discoveries[i];
                var when = d.DiscoveredAt.ToLocalTime().ToString("yyyy-MM-dd");
                list.Add($"{d.SourceType} — {d.SourceName} (on {when})");
            }
            return list;
        }
    }

    public CompanyProfileViewModel(ICompanyRepository companies, ICompanyProfiler profiler,
                                    ICompanyDiscoveryRepository discoveries,
                                    IJobRepository jobs)
    {
        _companies = companies;
        _profiler = profiler;
        _discoveries = discoveries;
        _jobs = jobs;
    }

    /// <summary>30-day churn for the loaded company, or null when the cohort is empty
    /// (no jobs first seen ≥30 days ago). Read by ChurnLine for display.</summary>
    public ChurnStat? Churn { get; private set; }

    /// <summary>METADATA-section row: "12% (8 of 65 from cohort)" or
    /// "n/a — no jobs ≥30 days old yet" when there's no cohort to measure against.</summary>
    public string ChurnLine
    {
        get
        {
            if (Churn is null) return "n/a — no jobs ≥30 days old yet";
            var c = Churn.Value;
            var pct = (int)System.Math.Round(c.ChurnPct);
            return $"{pct}% ({c.Inactive} of {c.CohortSize} from 30-day cohort inactive)";
        }
    }

    public void Load(int companyId)
    {
        Company = _companies.GetById(companyId);
        Profile = Company is null ? null : _companies.GetProfile(Company.Id);
        Discoveries = Company is null ? Array.Empty<CompanyDiscovery>()
                                       : _discoveries.GetByCompany(Company.Id);
        if (Company is null) { Churn = null; }
        else
        {
            // Whole-table batch query but reused at most once per dialog open — cheap enough.
            // If this dialog ever opens hundreds of times back-to-back we'd add a per-company variant.
            var all = _jobs.GetChurnRate30dByCompany();
            Churn = all.TryGetValue(Company.Id, out var s) ? s : (ChurnStat?)null;
        }
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
        OnPropertyChanged(nameof(DiscoveryLine));
        OnPropertyChanged(nameof(AdditionalDiscoveryLines));
        OnPropertyChanged(nameof(ChurnLine));
    }
}
