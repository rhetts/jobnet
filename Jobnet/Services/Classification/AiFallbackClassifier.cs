using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jobnet.Data.Repositories;
using Jobnet.Services.Ai;

namespace Jobnet.Services.Classification;

/// <summary>
/// AI-backed fallback classifier (Gemini or Claude — whichever ai_provider points to).
/// Used when HeuristicClassifier has no match. Sends the title and the current taxonomy
/// to the AI client; expects strict JSON output. Falls back gracefully if no provider
/// is configured.
/// </summary>
public sealed class AiFallbackClassifier : IJobClassifier
{
    private readonly IAiClient _ai;
    private readonly ILevelRepository _levels;
    private readonly IAreaRepository _areas;

    public AiFallbackClassifier(IAiClient ai, ILevelRepository levels, IAreaRepository areas)
    {
        _ai = ai;
        _levels = levels;
        _areas = areas;
    }

    public ClassificationResult Classify(string title, string? department = null)
    {
        if (!_ai.IsConfigured)
            return new ClassificationResult
            {
                LevelId = null, LevelName = null,
                Areas = Array.Empty<(int, string)>(),
                Source = "none",
                Reason = "No AI provider configured (set gemini_api_key or claude_api_key in Settings)"
            };

        var levels = _levels.GetAll();
        var areas  = _areas.GetAll();

        var system =
            "You classify job titles into a fixed taxonomy. Respond with strict JSON only — no prose, no markdown.\n" +
            "Output schema: {\"level\": \"<one of the levels>\", \"areas\": [\"<one or more areas>\"]}\n" +
            "If no level applies, set level to null. Areas can be empty.";
        var user =
            $"Job title: {title}\n" +
            (department is null ? "" : $"Department/team: {department}\n") +
            $"\nValid levels: {string.Join(", ", levels.Select(l => l.Name))}\n" +
            $"Valid areas: {string.Join(", ", areas.Select(a => a.Name))}\n";

        AiResponse response;
        try
        {
            response = _ai.CompleteAsync(user, system, maxTokens: 256, task: "classifier").GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return new ClassificationResult
            {
                LevelId = null, LevelName = null, Areas = Array.Empty<(int, string)>(),
                Source = "none",
                Reason = $"AI call failed ({_ai.ProviderId}): {ex.Message}"
            };
        }

        return Parse(response.Text, levels, areas, response.ProviderId, response.InputTokens, response.OutputTokens);
    }

    private static ClassificationResult Parse(string responseText, IReadOnlyList<Models.Level> levels, IReadOnlyList<Models.Area> areas, string providerId, int tokIn, int tokOut)
    {
        var levelByName = levels.ToDictionary(l => l.Name, l => l.Id, StringComparer.OrdinalIgnoreCase);
        var areaByName  = areas.ToDictionary(a => a.Name, a => a.Id, StringComparer.OrdinalIgnoreCase);

        var json = StripFences(responseText.Trim());

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? levelName = root.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.String ? lv.GetString() : null;
            int? levelId = levelName is not null && levelByName.TryGetValue(levelName, out var lid) ? lid : (int?)null;

            var matched = new List<(int Id, string Name)>();
            if (root.TryGetProperty("areas", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var elt in arr.EnumerateArray())
                {
                    if (elt.ValueKind != JsonValueKind.String) continue;
                    var name = elt.GetString();
                    if (name is null) continue;
                    if (areaByName.TryGetValue(name, out var aid))
                        matched.Add((aid, areas.First(a => a.Id == aid).Name));
                }
            }

            return new ClassificationResult
            {
                LevelId = levelId,
                LevelName = levelId.HasValue ? levels.First(l => l.Id == levelId.Value).Name : null,
                Areas = matched,
                Source = providerId,
                Reason = $"AI classification via {providerId} ({tokIn}/{tokOut} tokens)"
            };
        }
        catch (Exception ex)
        {
            return new ClassificationResult
            {
                LevelId = null, LevelName = null, Areas = Array.Empty<(int, string)>(),
                Source = "none",
                Reason = $"Could not parse {providerId} response: {ex.Message}; raw: {Truncate(responseText, 120)}"
            };
        }
    }

    private static string StripFences(string s)
    {
        if (s.StartsWith("```"))
        {
            var firstNl = s.IndexOf('\n');
            if (firstNl > 0) s = s[(firstNl + 1)..];
            if (s.EndsWith("```")) s = s[..^3];
        }
        return s.Trim();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
}
