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
    private readonly IDiscoverySeedRepository _seeds;
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

    // AI tab — single dropdown toggles the global provider mode.
    public System.Collections.Generic.IReadOnlyList<string> AiModes { get; } = new[] { "Online", "Local" };

    [ObservableProperty] private string _aiMode = "Online";
    [ObservableProperty] private string _geminiApiKey = string.Empty;
    [ObservableProperty] private string _geminiModel = "gemini-2.5-flash-lite";
    [ObservableProperty] private string _claudeApiKey = string.Empty;
    [ObservableProperty] private string _claudeModel = "claude-haiku-4-5";
    [ObservableProperty] private string _groqApiKey = string.Empty;
    [ObservableProperty] private string _groqModel = "llama-3.3-70b-versatile";
    [ObservableProperty] private string _llamaModelPath = string.Empty;
    [ObservableProperty] private string _llamaGpuLayers = "0";
    [ObservableProperty] private string _llamaContextSize = "4096";

    // Scraping tab
    [ObservableProperty] private int _scrapeDelayMs = 2000;
    [ObservableProperty] private string _claudeExtractionPrompt = string.Empty;

    // Data tab
    public string DatabasePath { get; }

    [ObservableProperty] private TaxonomyItemViewModel? _selectedLevel;
    [ObservableProperty] private TaxonomyItemViewModel? _selectedArea;

    public ObservableCollection<AggregatorToggleViewModel> Aggregators { get; } = new();
    public ObservableCollection<TaxonomyItemViewModel> Levels { get; } = new();
    public ObservableCollection<TaxonomyItemViewModel> Areas { get; } = new();
    public ObservableCollection<DiscoverySeedItemViewModel> DiscoverySeeds { get; } = new();
    [ObservableProperty] private DiscoverySeedItemViewModel? _selectedDiscoverySeed;

    private readonly HashSet<int> _deletedLevelIds = new();
    private readonly HashSet<int> _deletedAreaIds = new();
    private readonly HashSet<int> _deletedSeedIds = new();
    private readonly HashSet<int> _deletedAggregatorIds = new();

    [ObservableProperty] private AggregatorToggleViewModel? _selectedAggregator;

    public event Action? CloseRequested;

    public SettingsViewModel(IConfigRepository config, IAggregatorRepository aggregators,
                              ILevelRepository levels, IAreaRepository areas,
                              IDiscoverySeedRepository seeds, IAppPaths paths)
    {
        _config = config;
        _aggregators = aggregators;
        _levels = levels;
        _areas = areas;
        _seeds = seeds;
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
        var provider = _config.GetOrDefault("ai_provider", "gemini").ToLowerInvariant();
        // "Local" mode is whenever llama is the first token in the chain; everything else = "Online".
        var firstProvider = provider.Replace("+", ",").Split(',', StringSplitOptions.RemoveEmptyEntries)
                                    .Select(s => s.Trim()).FirstOrDefault() ?? "gemini";
        AiMode = firstProvider is "llama" or "local" ? "Local" : "Online";
        LlamaModelPath = _config.GetOrDefault("llama_model_path", "");
        LlamaGpuLayers = _config.GetOrDefault("llama_gpu_layers", "0");
        LlamaContextSize = _config.GetOrDefault("llama_context_size", "4096");
        GeminiApiKey            = _config.GetOrDefault("gemini_api_key", "");
        GeminiModel             = _config.GetOrDefault("gemini_model", "gemini-2.5-flash-lite");
        ClaudeApiKey            = _config.GetOrDefault("claude_api_key", "");
        ClaudeModel             = _config.GetOrDefault("claude_model", "claude-haiku-4-5");
        GroqApiKey              = _config.GetOrDefault("groq_api_key", "");
        GroqModel               = _config.GetOrDefault("groq_model", "llama-3.3-70b-versatile");
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

        DiscoverySeeds.Clear();
        foreach (var s in _seeds.GetAll())
            DiscoverySeeds.Add(new DiscoverySeedItemViewModel(s.Id, s.Name, s.Url, s.Description, s.IsEnabled, s.MaxPages));
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
        // Mode dropdown → provider chain. Online uses the cloud fallback chain (gemini first,
        // groq second). Local uses the in-process llama model. Per-task overrides are still
        // honored if set via CLI; this only writes the global default.
        var chain = AiMode == "Local" ? "llama" : "gemini,groq";
        _config.Set("ai_provider",               chain);
        _config.Set("llama_model_path",          LlamaModelPath ?? "");
        if (int.TryParse(LlamaGpuLayers, out var gl) && gl >= 0)
            _config.Set("llama_gpu_layers",      gl.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (int.TryParse(LlamaContextSize, out var cs) && cs > 0)
            _config.Set("llama_context_size",    cs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _config.Set("gemini_api_key",            GeminiApiKey ?? "");
        _config.Set("gemini_model",              GeminiModel ?? "gemini-2.5-flash-lite");
        _config.Set("claude_api_key",            ClaudeApiKey ?? "");
        _config.Set("claude_model",              ClaudeModel ?? "claude-haiku-4-5");
        _config.Set("groq_api_key",              GroqApiKey ?? "");
        _config.Set("groq_model",                GroqModel ?? "llama-3.3-70b-versatile");
        _config.Set("claude_extraction_prompt",  ClaudeExtractionPrompt ?? "");

        // Aggregators (Boards): delete-then-upsert in the same pattern as directory seeds.
        foreach (var id in _deletedAggregatorIds) _aggregators.Delete(id);
        _deletedAggregatorIds.Clear();
        foreach (var a in Aggregators)
        {
            var name = (a.Name ?? "").Trim();
            var baseUrl = (a.BaseUrl ?? "").Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(baseUrl)) continue;
            var notes = string.IsNullOrWhiteSpace(a.Notes) ? null : a.Notes.Trim();
            var pages = Math.Max(1, a.MaxPages);
            if (a.IsNew)
                _aggregators.Insert(name, baseUrl, notes, a.IsEnabled, pages);
            else
                _aggregators.Update(a.Id, name, baseUrl, notes, a.IsEnabled, pages);
        }

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

        // Discovery seeds
        foreach (var id in _deletedSeedIds) _seeds.Delete(id);
        _deletedSeedIds.Clear();
        for (var i = 0; i < DiscoverySeeds.Count; i++)
        {
            var s = DiscoverySeeds[i];
            if (string.IsNullOrWhiteSpace(s.Name) || string.IsNullOrWhiteSpace(s.Url)) continue;
            if (s.IsNew)
                _seeds.Insert(s.Name.Trim(), s.Url.Trim(),
                               string.IsNullOrWhiteSpace(s.Description) ? null : s.Description.Trim(),
                               s.IsEnabled, sortOrder: i, maxPages: Math.Max(1, s.MaxPages));
            else
                _seeds.Update(s.Id, s.Name.Trim(), s.Url.Trim(),
                               string.IsNullOrWhiteSpace(s.Description) ? null : s.Description.Trim(),
                               s.IsEnabled, sortOrder: i, maxPages: Math.Max(1, s.MaxPages));
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
    private void BrowseLlamaModel()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select a .gguf model file",
            Filter = "GGUF model files (*.gguf)|*.gguf|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == true) LlamaModelPath = dlg.FileName;
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

    [RelayCommand]
    private void AddDiscoverySeed()
    {
        var item = new DiscoverySeedItemViewModel(0, "(new source)", "https://", "", true, maxPages: 1);
        DiscoverySeeds.Add(item);
        SelectedDiscoverySeed = item;
    }

    [RelayCommand]
    private void DeleteDiscoverySeed()
    {
        if (SelectedDiscoverySeed is null) return;
        if (!SelectedDiscoverySeed.IsNew) _deletedSeedIds.Add(SelectedDiscoverySeed.Id);
        DiscoverySeeds.Remove(SelectedDiscoverySeed);
        SelectedDiscoverySeed = null;
    }

    [RelayCommand]
    private void AddAggregator()
    {
        var item = new AggregatorToggleViewModel(new Models.AggregatorSource
        {
            Id = 0, Name = "(new board)", BaseUrl = "https://", IsEnabled = false, Notes = "", MaxPages = 1
        });
        Aggregators.Add(item);
        SelectedAggregator = item;
    }

    [RelayCommand]
    private void DeleteAggregator()
    {
        if (SelectedAggregator is null) return;
        if (!SelectedAggregator.IsNew) _deletedAggregatorIds.Add(SelectedAggregator.Id);
        Aggregators.Remove(SelectedAggregator);
        SelectedAggregator = null;
    }

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
