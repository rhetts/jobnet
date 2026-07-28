using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Jobnet.Services.Profiling;

/// <summary>Closed enum of company sizes. Strings are persisted in <c>companies.size_category</c>.</summary>
public static class CompanySizeCategories
{
    public const string Startup  = "startup";   // 1-50
    public const string Growth   = "growth";    // 51-200
    public const string MidSize  = "mid_size";  // 201-1000
    public const string Large    = "large";     // 1000+

    /// <summary>Display label for the chip / filter UI.</summary>
    public static string Label(string? key) => key switch
    {
        Startup => "Startup (≤50)",
        Growth  => "Growth (51-200)",
        MidSize => "Mid-size (201-1000)",
        Large   => "Large (1000+)",
        _       => "Unknown"
    };

    public static readonly string[] All = { Startup, Growth, MidSize, Large };
}

/// <summary>Classifies a freeform AI-derived size hint into one of the closed
/// <see cref="CompanySizeCategories"/> values. Returns null when the hint is missing
/// or doesn't carry a usable signal (e.g. "over 2,200 customers" — that's customer count,
/// not employee count, so we'd rather say "unknown" than guess wrong).
/// </summary>
public static class CompanySizeClassifier
{
    // Numbers like 50, 1,000, 10000, plus optional + or k suffix. Handles common AI outputs:
    //   "50-200", "1000+", "10,000-50,000", "200 employees", "5k", "5000"
    private static readonly Regex NumberRange = new(
        @"(?<low>\d[\d,]*)\s*(?<lowK>[kK])?(?:\s*[-–to]+\s*(?<high>\d[\d,]*)\s*(?<highK>[kK])?)?\s*(?<plus>\+)?",
        RegexOptions.Compiled);

    private static readonly Regex CustomerNoise = new(
        @"\b(?:customer|merchant|user|client|alumni|investment|patient|product|million|billion)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Run the classifier. Returns one of the constants in
    /// <see cref="CompanySizeCategories"/> or <c>null</c> when no usable signal is present.</summary>
    public static string? Classify(string? sizeHint)
    {
        if (string.IsNullOrWhiteSpace(sizeHint)) return null;

        var hint = sizeHint.Trim();

        // Bail on hints that name customers / merchants / users — those are scale metrics,
        // not employee counts, and the AI mis-extracts them often enough to be a real signal-
        // killer if we trust them.
        if (CustomerNoise.IsMatch(hint)) return null;

        // Word-form hints first — short-circuits so "small" doesn't get parsed as zero.
        var lower = hint.ToLowerInvariant();
        if (lower.Contains("not specified") || lower.Contains("not mentioned") || lower is "null" or "none") return null;
        if (lower.Contains("startup") || lower is "small" or "tiny" || lower.Contains("under 50")) return CompanySizeCategories.Startup;
        if (lower is "medium" || lower is "mid") return CompanySizeCategories.MidSize;
        if (lower.Contains("enterprise") || lower is "huge" or "massive" || lower.Contains("thousands") || lower.Contains("tens of thousands"))
            return CompanySizeCategories.Large;

        var m = NumberRange.Match(hint);
        if (!m.Success) return null;

        if (!TryParseNum(m.Groups["low"].Value, m.Groups["lowK"].Success, out var low)) return null;
        var highHasValue = m.Groups["high"].Success
                           && TryParseNum(m.Groups["high"].Value, m.Groups["highK"].Success, out var high);
        var plus = m.Groups["plus"].Success;

        // For a range, the LOW end determines the bucket — if someone says "50-200" they're
        // really telling us "between growth and mid-size start" and the conservative answer
        // is "growth". For "1000+", the plus signals open-ended large.
        var n = highHasValue ? Math.Max(low, 0) : (plus ? Math.Max(low, 0) : low);

        return n switch
        {
            <= 50    => CompanySizeCategories.Startup,
            <= 200   => CompanySizeCategories.Growth,
            <= 1000  => CompanySizeCategories.MidSize,
            _        => CompanySizeCategories.Large
        };
    }

    private static bool TryParseNum(string raw, bool isK, out int value)
    {
        value = 0;
        var cleaned = raw.Replace(",", "");
        if (!int.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return false;
        if (isK) value *= 1000;
        return true;
    }
}
