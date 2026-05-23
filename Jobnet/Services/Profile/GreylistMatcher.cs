using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jobnet.Services.Profile;

/// <summary>
/// Parses the user's greylist keyword string and matches it against job fields. A "match" means
/// any greylist token appears as a whole word (case-insensitive) in any of the supplied fields.
///
/// Tokens are split on commas, newlines, and semicolons. Empty tokens are skipped. Each token is
/// compiled to a regex with word-boundary anchors so "no" doesn't match "noted" and "senior"
/// doesn't match "seniority". Multi-word phrases like "tax season" are kept whole.
/// </summary>
public static class GreylistMatcher
{
    /// <summary>Parse the raw greylist string into compiled regexes. Returns empty list if the
    /// input is null/whitespace. Safe to call per-call; for hot loops, cache the result.</summary>
    public static IReadOnlyList<Regex> Parse(string? rawGreylist)
    {
        if (string.IsNullOrWhiteSpace(rawGreylist)) return Array.Empty<Regex>();
        var tokens = rawGreylist
            .Split(new[] { ',', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var list = new List<Regex>(tokens.Count);
        foreach (var t in tokens)
        {
            // \b is word-boundary. The Escape handles regex-special chars in the token.
            // Compiled flag — these are reused per job during a sweep.
            try
            {
                list.Add(new Regex($@"\b{Regex.Escape(t)}\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));
            }
            catch
            {
                // Skip tokens that fail to compile (unlikely after Escape, but defensive).
            }
        }
        return list;
    }

    /// <summary>True if any compiled token matches any of the supplied fields. Null/empty fields
    /// are skipped. Returns false on empty token list.</summary>
    public static bool MatchesAny(IReadOnlyList<Regex> tokens, params string?[] fields)
    {
        if (tokens.Count == 0) return false;
        foreach (var field in fields)
        {
            if (string.IsNullOrEmpty(field)) continue;
            foreach (var rx in tokens)
                if (rx.IsMatch(field)) return true;
        }
        return false;
    }
}
