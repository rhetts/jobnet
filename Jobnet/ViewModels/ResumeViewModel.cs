using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jobnet.Data.Repositories;
using Jobnet.Services.Resume;

namespace Jobnet.ViewModels;

/// <summary>Backs the Profile window. Holds the uploaded resume, the candidate's preferred
/// areas/levels, and free-text boost keywords. All three are surfaced to the matcher prompt
/// so AI scoring weights jobs toward what the candidate actually wants, not just the passive
/// reading of the resume text.</summary>
public partial class ResumeViewModel : ObservableObject
{
    private readonly IResumeMatcher _matcher;
    private readonly IConfigRepository _config;
    private readonly ILevelRepository _levels;
    private readonly IAreaRepository _areas;
    private readonly IJobRepository _jobs;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UploadCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _statusLine = "(no resume loaded)";
    [ObservableProperty] private string _resumeText = "";
    [ObservableProperty] private string _resultLine = "";
    [ObservableProperty] private bool _hasResume;
    [ObservableProperty] private string _boostKeywords = "";
    [ObservableProperty] private string _greylistKeywords = "";

    public ObservableCollection<FilterItemViewModel> PreferredAreas { get; } = new();
    public ObservableCollection<FilterItemViewModel> PreferredLevels { get; } = new();

    /// <summary>Raised after upload or save so the main window can reload (resume change clears
    /// match scores; preference change should re-score on next match run).</summary>
    public event Action? Completed;

    public ResumeViewModel(IResumeMatcher matcher, IConfigRepository config,
                            ILevelRepository levels, IAreaRepository areas,
                            IJobRepository jobs)
    {
        _matcher = matcher;
        _config = config;
        _levels = levels;
        _areas = areas;
        _jobs = jobs;
        Reload();
    }

    private void Reload()
    {
        var text = _matcher.GetStoredResume();
        var path = _matcher.GetStoredResumeSourcePath();
        if (string.IsNullOrEmpty(text))
        {
            StatusLine = "(no resume loaded)";
            ResumeText = "";
            HasResume = false;
        }
        else
        {
            StatusLine = $"Loaded {text!.Length} chars" + (string.IsNullOrEmpty(path) ? "" : $" from {System.IO.Path.GetFileName(path)}");
            ResumeText = text;
            HasResume = true;
        }

        var savedAreas = ParseIdSet(_config.GetOrDefault("profile_preferred_area_ids", "[]"));
        var savedLevels = ParseIdSet(_config.GetOrDefault("profile_preferred_level_ids", "[]"));
        BoostKeywords    = _config.GetOrDefault("profile_boost_keywords",    "");
        GreylistKeywords = _config.GetOrDefault("profile_greylist_keywords", "");

        PreferredAreas.Clear();
        foreach (var a in _areas.GetAll())
            PreferredAreas.Add(new FilterItemViewModel(a.Id, a.Name, savedAreas.Contains(a.Id), () => { }));

        PreferredLevels.Clear();
        foreach (var l in _levels.GetAll())
            PreferredLevels.Add(new FilterItemViewModel(l.Id, l.Name, savedLevels.Contains(l.Id), () => { }));
    }

    private bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task UploadAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select your resume PDF",
            Filter = "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;

        IsBusy = true;
        ResultLine = $"Parsing {System.IO.Path.GetFileName(dlg.FileName)}...";
        try
        {
            var r = await Task.Run(() => _matcher.UploadResumeAsync(dlg.FileName)).ConfigureAwait(true);
            if (r.Success)
            {
                ResultLine = $"Loaded {r.Pages} page(s), {r.Characters} chars. Previous match scores cleared. " +
                             "Run match from the Refresh dialog to score everything.";
                Reload();
                Completed?.Invoke();
            }
            else
            {
                ResultLine = $"Upload failed: {r.Error}";
            }
        }
        catch (Exception ex) { ResultLine = $"Failed: {ex.GetType().Name}: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void SaveProfile()
    {
        var areaIds = PreferredAreas.Where(a => a.IsSelected).Select(a => a.Id).ToArray();
        var levelIds = PreferredLevels.Where(l => l.IsSelected).Select(l => l.Id).ToArray();
        _config.Set("profile_preferred_area_ids",  JsonSerializer.Serialize(areaIds));
        _config.Set("profile_preferred_level_ids", JsonSerializer.Serialize(levelIds));
        _config.Set("profile_boost_keywords",      (BoostKeywords ?? "").Trim());
        _config.Set("profile_greylist_keywords",   (GreylistKeywords ?? "").Trim());

        // Sweep retroactively so adding a new greylist token immediately downvotes any matching
        // jobs in the current set. Only touches Neutral active jobs — never overrides Approved
        // or already-downvoted state.
        var downvoted = 0;
        try { downvoted = _jobs.ApplyGreylist(GreylistKeywords); }
        catch (Exception ex) { ResultLine = $"Saved, but greylist sweep failed: {ex.Message}"; return; }

        var suffix = downvoted > 0
            ? $" Greylist swept {downvoted} matching job{(downvoted == 1 ? "" : "s")} → downvoted."
            : "";
        ResultLine = $"Profile saved at {DateTime.Now:HH:mm:ss}.{suffix} Re-run match in the Refresh dialog to apply scoring changes.";
        if (downvoted > 0) Completed?.Invoke();
    }

    private static HashSet<int> ParseIdSet(string json)
    {
        var set = new HashSet<int>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var e in doc.RootElement.EnumerateArray())
                    if (e.TryGetInt32(out var i)) set.Add(i);
        }
        catch { }
        return set;
    }
}
