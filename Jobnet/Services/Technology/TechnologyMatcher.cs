using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jobnet.Data.Repositories;

namespace Jobnet.Services.Technology;

/// <summary>
/// Scans free-form text (job title + description + summary) and returns the set of
/// technology IDs that appear. Whole-word + case-insensitive matching via a regex per
/// alias, compiled once at service construction. Reloading aliases requires a service
/// restart — that's acceptable since the vocabulary is migration-managed, not user-edited.
/// </summary>
public interface ITechnologyMatcher
{
    /// <summary>Return the distinct set of technology IDs that match anywhere in the text.
    /// Null/empty text returns an empty set. Multi-alias technologies count once.</summary>
    IReadOnlyList<int> Match(string? text);
}

public sealed class TechnologyMatcher : ITechnologyMatcher
{
    private readonly List<(Regex Re, int TechId)> _patterns;

    public TechnologyMatcher(ITechnologyRepository repo)
    {
        var aliasMap = repo.GetAliasMap();
        _patterns = new List<(Regex, int)>(aliasMap.Count);
        foreach (var (alias, techId) in aliasMap)
        {
            if (string.IsNullOrWhiteSpace(alias)) continue;
            var pattern = BuildPattern(alias);
            try
            {
                _patterns.Add((new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled), techId));
            }
            catch (ArgumentException)
            {
                // Skip malformed aliases (shouldn't happen with migration-managed seed data,
                // but guard against future additions like "C++" before they're escape-tested).
            }
        }
    }

    public IReadOnlyList<int> Match(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<int>();
        var hits = new HashSet<int>();
        foreach (var (re, techId) in _patterns)
            if (re.IsMatch(text)) hits.Add(techId);
        return hits.Count == 0 ? Array.Empty<int>() : hits.ToList();
    }

    /// <summary>Build a whole-word, case-insensitive regex for an alias. Word boundaries are
    /// tricky when the alias starts or ends with a non-word character (e.g. ".NET", "C#",
    /// "C++"). For those, swap \b for a lookahead/lookbehind that only requires the
    /// adjacent character to NOT be a letter/digit, so ".NET" matches in "use .NET 8"
    /// but not in "node.netflix.com".</summary>
    private static string BuildPattern(string alias)
    {
        var escaped = Regex.Escape(alias);
        var startsWithWord = char.IsLetterOrDigit(alias[0]);
        var endsWithWord   = char.IsLetterOrDigit(alias[^1]);
        var left  = startsWithWord ? @"\b"   : @"(?<![A-Za-z0-9])";
        var right = endsWithWord   ? @"\b"   : @"(?![A-Za-z0-9])";
        return $"{left}{escaped}{right}";
    }
}
