using System;
using System.Collections.Generic;
using System.Linq;
using Jobnet.Data.Repositories;

namespace Jobnet.Services.Classification;

/// <summary>
/// Keyword-based classifier — fast, free, and correct on ~80% of well-formed tech job titles.
/// Maps to names found in the levels/areas tables; never invents new categories.
/// </summary>
public sealed class HeuristicClassifier : IJobClassifier
{
    private readonly ILevelRepository _levels;
    private readonly IAreaRepository _areas;

    public HeuristicClassifier(ILevelRepository levels, IAreaRepository areas)
    {
        _levels = levels;
        _areas = areas;
    }

    // Order matters: more specific patterns first.
    private static readonly (string Pattern, string LevelName)[] LevelRules =
    {
        ("vice president",       "VP+"),
        ("vp ",                  "VP+"),
        ("vp,",                  "VP+"),
        ("director",             "Director"),
        ("staff ",               "Staff / Principal"),
        ("principal ",           "Staff / Principal"),
        ("senior staff",         "Staff / Principal"),
        ("sr. staff",            "Staff / Principal"),
        ("lead ",                "Lead"),
        (", lead",               "Lead"),
        ("engineering manager",  "Manager"),
        (" manager",             "Manager"),
        ("manager,",             "Manager"),
        ("senior ",              "Senior"),
        ("sr. ",                 "Senior"),
        ("sr ",                  "Senior"),
        ("junior ",              "Junior"),
        ("jr. ",                 "Junior"),
        ("jr ",                  "Junior"),
        ("intern",               "Junior"),
        ("entry-level",          "Junior"),
        ("entry level",          "Junior"),
        ("mid-level",            "Mid"),
        (" iv ",                 "Staff / Principal"),
        (" iii ",                "Senior"),
        (" ii ",                 "Mid"),
        ("founding",             "Staff / Principal"),
    };

    // Areas can return multiple matches; order doesn't matter for priority but the first match wins for primary tag.
    private static readonly (string Pattern, string AreaName)[] AreaRules =
    {
        ("security",             "Security"),
        ("pentest",              "Security"),
        ("application security", "Security"),
        ("appsec",               "Security"),
        ("data engineer",        "Data / ML"),
        ("data scientist",       "Data / ML"),
        ("data analyst",         "Data / ML"),
        ("ml engineer",          "Data / ML"),
        ("machine learning",     "Data / ML"),
        ("analytics",            "Data / ML"),
        ("devops",               "DevOps / Platform"),
        ("sre",                  "DevOps / Platform"),
        ("site reliability",     "DevOps / Platform"),
        ("platform engineer",    "DevOps / Platform"),
        ("infrastructure",       "DevOps / Platform"),
        ("network engineer",     "DevOps / Platform"),
        ("cloud engineer",       "DevOps / Platform"),
        ("qa ",                  "QA / Test"),
        ("quality assurance",    "QA / Test"),
        ("test engineer",        "QA / Test"),
        ("automation engineer",  "QA / Test"),
        ("sdet",                 "QA / Test"),
        ("product manager",      "Product Management"),
        ("product owner",        "Product Management"),
        (" pm,",                 "Product Management"),
        (" pm ",                 "Product Management"),
        ("designer",             "Design"),
        ("ux ",                  "Design"),
        ("ui ",                  "Design"),
        ("director of engineering","Management"),
        ("engineering manager",  "Management"),
        ("vp engineering",       "Management"),
        ("backend engineer",     "Software Engineering"),
        ("frontend",             "Software Engineering"),
        ("full stack",           "Software Engineering"),
        ("full-stack",           "Software Engineering"),
        ("fullstack",            "Software Engineering"),
        ("software engineer",    "Software Engineering"),
        ("software developer",   "Software Engineering"),
        ("ios engineer",         "Software Engineering"),
        ("android engineer",     "Software Engineering"),
        ("mobile engineer",      "Software Engineering"),
        ("blockchain",           "Software Engineering"),
        ("rails engineer",       "Software Engineering"),
        ("api engineer",         "Software Engineering"),
        ("developer",            "Software Engineering"),
        ("engineer",             "Software Engineering"),
        ("marketing",            "Other"),
        ("sales ",               "Other"),
        ("bookkeeper",           "Other"),
        ("recruiter",            "Other"),
        ("customer success",     "Other"),
        ("store manager",        "Other"),
        ("project manager",      "Management"),
    };

    public ClassificationResult Classify(string title, string? department = null)
    {
        var levels = _levels.GetAll().ToDictionary(l => l.Name, l => l.Id, StringComparer.OrdinalIgnoreCase);
        var areas  = _areas.GetAll().ToDictionary(a => a.Name, a => a.Id, StringComparer.OrdinalIgnoreCase);

        var hay = " " + (title ?? "").ToLowerInvariant() + " ";
        if (!string.IsNullOrWhiteSpace(department))
            hay += " " + department.ToLowerInvariant() + " ";

        // Level: first match wins.
        string? levelName = null;
        int? levelId = null;
        string? levelHit = null;
        foreach (var (pattern, name) in LevelRules)
        {
            if (hay.Contains(pattern, StringComparison.Ordinal))
            {
                levelName = name;
                levelHit = pattern.Trim();
                if (levels.TryGetValue(name, out var id)) levelId = id;
                break;
            }
        }

        // Default to "Mid" if title clearly an engineering role with no level prefix — common in industry.
        if (levelId is null && IsLikelyEngineeringTitle(hay))
        {
            if (levels.TryGetValue("Mid", out var midId))
            {
                levelId = midId;
                levelName = "Mid";
                levelHit = "(default for unmarked engineering title)";
            }
        }

        // Areas: collect every match, dedupe.
        var matchedAreas = new List<(int Id, string Name)>();
        var matchedAreaNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var areaHits = new List<string>();
        foreach (var (pattern, name) in AreaRules)
        {
            if (hay.Contains(pattern, StringComparison.Ordinal) && matchedAreaNames.Add(name))
            {
                if (areas.TryGetValue(name, out var id))
                {
                    matchedAreas.Add((id, name));
                    areaHits.Add($"'{pattern.Trim()}'→{name}");
                }
            }
        }

        var matched = levelId.HasValue || matchedAreas.Count > 0;
        var reason = matched
            ? $"level: {levelHit ?? "none"}; areas: {(areaHits.Count == 0 ? "none" : string.Join(", ", areaHits))}"
            : "no heuristic match";

        return new ClassificationResult
        {
            LevelId = levelId,
            LevelName = levelName,
            Areas = matchedAreas,
            Source = matched ? "heuristic" : "none",
            Reason = reason
        };
    }

    private static bool IsLikelyEngineeringTitle(string lowerHay) =>
        lowerHay.Contains("engineer", StringComparison.Ordinal) ||
        lowerHay.Contains("developer", StringComparison.Ordinal);
}
