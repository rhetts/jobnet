using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Jobnet.Services;

namespace Jobnet.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IConfigRepository _config;
    private readonly IAggregatorRepository _aggregators;
    private readonly ILevelRepository _levels;
    private readonly IAreaRepository _areas;
    private readonly IAppPaths _paths;

    // Search tab
    [ObservableProperty] private string _googleApiKey = string.Empty;
    [ObservableProperty] private string _googleEngineId = string.Empty;
    [ObservableProperty] private string _braveApiKey = string.Empty;
    [ObservableProperty] private string _searchEngine = "brave_search";
    [ObservableProperty] private string _citiesText = string.Empty;

    // Scoring tab
    [ObservableProperty] private double _scoreWeightArea = 0.5;
    [ObservableProperty] private double _scoreWeightLevel = 0.5;

    // Scraping tab
    [ObservableProperty] private int _scrapeDelayMs = 2000;
    [ObservableProperty] private string _claudeCliPath = string.Empty;
    [ObservableProperty] private string _claudeExtractionPrompt = string.Empty;

    // Data tab
    public string DatabasePath { get; }

    [ObservableProperty] private TaxonomyItemViewModel? _selectedLevel;
    [ObservableProperty] private TaxonomyItemViewModel? _selectedArea;

    public ObservableCollection<AggregatorToggleViewModel> Aggregators { get; } = new();
    public ObservableCollection<TaxonomyItemViewModel> Levels { get; } = new();
    public ObservableCollection<TaxonomyItemViewModel> Areas { get; } = new();

    private readonly HashSet<int> _deletedLevelIds = new();
    private readonly HashSet<int> _deletedAreaIds = new();

    public event Action? CloseRequested;

    public SettingsViewModel(IConfigRepository config, IAggregatorRepository aggregators,
                              ILevelRepository levels, IAreaRepository areas, IAppPaths paths)
    {
        _config = config;
        _aggregators = aggregators;
        _levels = levels;
        _areas = areas;
        _paths = paths;
        DatabasePath = paths.DatabasePath;

        Load();
    }

    private void Load()
    {
        GoogleApiKey            = _config.GetOrDefault("google_cse_api_key", "");
        GoogleEngineId          = _config.GetOrDefault("google_cse_engine_id", "");
        BraveApiKey             = _config.GetOrDefault("brave_search_api_key", "");
        SearchEngine            = _config.GetOrDefault("search_engine", "brave_search");
        CitiesText              = JoinJsonList(_config.GetOrDefault("cities", "[]"));
        ScoreWeightArea         = ParseDouble(_config.GetOrDefault("score_weight_area", "0.5"));
        ScoreWeightLevel        = ParseDouble(_config.GetOrDefault("score_weight_level", "0.5"));
        ScrapeDelayMs           = ParseInt(_config.GetOrDefault("scrape_delay_ms", "2000"));
        ClaudeCliPath           = _config.GetOrDefault("claude_cli_path", "");
        ClaudeExtractionPrompt  = _config.GetOrDefault("claude_extraction_prompt", "");

        Aggregators.Clear();
        foreach (var a in _aggregators.GetAll())
            Aggregators.Add(new AggregatorToggleViewModel(a));

        Levels.Clear();
        foreach (var l in _levels.GetAll())
            Levels.Add(new TaxonomyItemViewModel(l.Id, l.Name));

        Areas.Clear();
        foreach (var a in _areas.GetAll())
            Areas.Add(new TaxonomyItemViewModel(a.Id, a.Name));
    }

    [RelayCommand]
    private void Save()
    {
        _config.Set("google_cse_api_key",        GoogleApiKey ?? "");
        _config.Set("google_cse_engine_id",      GoogleEngineId ?? "");
        _config.Set("brave_search_api_key",      BraveApiKey ?? "");
        _config.Set("search_engine",             SearchEngine ?? "brave_search");
        _config.Set("cities",                    SplitToJsonList(CitiesText));
        _config.Set("score_weight_area",         ScoreWeightArea.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        _config.Set("score_weight_level",        ScoreWeightLevel.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        _config.Set("scrape_delay_ms",           ScrapeDelayMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _config.Set("claude_cli_path",           ClaudeCliPath ?? "");
        _config.Set("claude_extraction_prompt",  ClaudeExtractionPrompt ?? "");

        foreach (var a in Aggregators)
            _aggregators.SetEnabled(a.Source.Id, a.IsEnabled);

        // Levels: deletes, then upserts in display order
        foreach (var id in _deletedLevelIds) _levels.Delete(id);
        _deletedLevelIds.Clear();
        var levelOrder = new List<int>();
        for (var i = 0; i < Levels.Count; i++)
        {
            var item = Levels[i];
            if (item.IsNew && !string.IsNullOrWhiteSpace(item.Name))
            {
                var newId = _levels.Insert(item.Name.Trim(), i);
                levelOrder.Add(newId);
            }
            else if (!item.IsNew)
            {
                _levels.Update(new Level { Id = item.Id, Name = item.Name.Trim(), SortOrder = i });
                levelOrder.Add(item.Id);
            }
        }

        foreach (var id in _deletedAreaIds) _areas.Delete(id);
        _deletedAreaIds.Clear();
        for (var i = 0; i < Areas.Count; i++)
        {
            var item = Areas[i];
            if (item.IsNew && !string.IsNullOrWhiteSpace(item.Name))
                _areas.Insert(item.Name.Trim(), i);
            else if (!item.IsNew)
                _areas.Update(new Area { Id = item.Id, Name = item.Name.Trim(), SortOrder = i });
        }

        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _paths.DataDirectory,
                UseShellExecute = true
            });
        }
        catch { }
    }

    [RelayCommand]
    private void AddLevel()
    {
        var item = new TaxonomyItemViewModel(0, "(new level)");
        Levels.Add(item);
        SelectedLevel = item;
    }

    [RelayCommand]
    private void DeleteLevel()
    {
        if (SelectedLevel is null) return;
        if (!SelectedLevel.IsNew) _deletedLevelIds.Add(SelectedLevel.Id);
        Levels.Remove(SelectedLevel);
        SelectedLevel = null;
    }

    [RelayCommand]
    private void MoveLevelUp() => MoveSelected(Levels, SelectedLevel, -1);

    [RelayCommand]
    private void MoveLevelDown() => MoveSelected(Levels, SelectedLevel, +1);

    [RelayCommand]
    private void AddArea()
    {
        var item = new TaxonomyItemViewModel(0, "(new area)");
        Areas.Add(item);
        SelectedArea = item;
    }

    [RelayCommand]
    private void DeleteArea()
    {
        if (SelectedArea is null) return;
        if (!SelectedArea.IsNew) _deletedAreaIds.Add(SelectedArea.Id);
        Areas.Remove(SelectedArea);
        SelectedArea = null;
    }

    [RelayCommand]
    private void MoveAreaUp() => MoveSelected(Areas, SelectedArea, -1);

    [RelayCommand]
    private void MoveAreaDown() => MoveSelected(Areas, SelectedArea, +1);

    private static void MoveSelected(ObservableCollection<TaxonomyItemViewModel> list, TaxonomyItemViewModel? selected, int delta)
    {
        if (selected is null) return;
        var idx = list.IndexOf(selected);
        var newIdx = idx + delta;
        if (newIdx < 0 || newIdx >= list.Count) return;
        list.Move(idx, newIdx);
    }

    private static string JoinJsonList(string json)
    {
        try
        {
            var items = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            return string.Join(Environment.NewLine, items);
        }
        catch { return string.Empty; }
    }

    private static string SplitToJsonList(string text)
    {
        var items = (text ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        return JsonSerializer.Serialize(items);
    }

    private static int ParseInt(string s) =>
        int.TryParse(s, System.Globalization.NumberStyles.Integer,
                     System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double ParseDouble(string s) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
}
