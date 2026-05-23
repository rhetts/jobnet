using System;
using System.Text.RegularExpressions;

namespace Jobnet.Services.AtsAdapters;

/// <summary>
/// Cleans raw job description text into a UI-friendly snippet. Two passes:
///   1. Strip HTML tags, decode entities, collapse whitespace.
///   2. Try to skip past the company-pitch boilerplate by finding a section header that marks
///      the start of the actual role content ("What you'll do", "Responsibilities", etc.).
///      If found, the snippet starts from that header. If not, returns the full cleaned text
///      truncated to the cap — caller may still see the company intro but at least it's plain text.
///
/// Used at both ingest time (Greenhouse/Lever/Ashby/JsonLd/Playwright adapters) and display time
/// (JobViewModel.ExpandedText fallback) so existing rows with HTML get cleaned on the fly.
/// </summary>
public static class SnippetCleaner
{
    private static readonly Regex TagRe        = new(@"<[^>]+>",                              RegexOptions.Compiled);
    private static readonly Regex WhitespaceRe = new(@"\s+",                                  RegexOptions.Compiled);
    // Common section headers that anchor the start of the actual job content. Ordered by
    // specificity / commonness in real ATS descriptions. We search case-insensitively against
    // the cleaned (post-strip) text.
    private static readonly string[] RoleHeaders =
    {
        "what you'll do",
        "what you will do",
        "what you'll be doing",
        "in this role you will",
        "in this role, you will",
        "in this role you'll",
        "in this role,",
        "in this role",
        "your role",
        "your responsibilities",
        "key responsibilities",
        "responsibilities:",
        "responsibilities",
        "about the role",
        "about the job",
        "about this role",
        "the role",
        "the opportunity",
        "the job",
        "day-to-day",
        "day to day",
        "you will",
        "you'll",
    };

    /// <summary>Strip HTML, decode entities, collapse whitespace, and skip the company-pitch
    /// preamble. Returns up to <paramref name="maxChars"/> characters. Null/blank input returns null.</summary>
    public static string? Clean(string? raw, int maxChars = 500)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var s = TagRe.Replace(raw, " ");
        s = System.Net.WebUtility.HtmlDecode(s);
        s = WhitespaceRe.Replace(s, " ").Trim();
        if (s.Length == 0) return null;

        s = SkipCompanyIntro(s);
        if (s.Length > maxChars) s = s.Substring(0, maxChars).TrimEnd() + "…";
        return s;
    }

    /// <summary>Find the earliest role-content header and return the substring from there. If no
    /// header is found (or only at the very end), returns the original text unchanged.</summary>
    private static string SkipCompanyIntro(string text)
    {
        var lower = text.ToLowerInvariant();
        var bestIdx = -1;
        foreach (var marker in RoleHeaders)
        {
            var idx = lower.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) continue;
            // Require the marker to appear within the first 80% of the text — otherwise it's
            // probably in the qualifications/about-us trailer, not at the start of the role section.
            if (idx > (text.Length * 0.8)) continue;
            if (bestIdx < 0 || idx < bestIdx) bestIdx = idx;
        }
        return bestIdx > 0 ? text.Substring(bestIdx) : text;
    }
}
