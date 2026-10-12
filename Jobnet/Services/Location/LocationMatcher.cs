using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jobnet.Services.Location;

/// <summary>
/// Classify whether a job's posted location is acceptable for a Vancouver-area job hunt.
/// Includes Metro Vancouver cities, broader BC, and remote-North-America roles.
/// Blank/unknown locations pass through (rather than dropping legit local roles
/// that didn't supply a location).
/// </summary>
public static class LocationMatcher
{
    // Substrings that mark a location as Vancouver-area.
    // Lowercased; matched as case-insensitive substrings on the normalised location text.
    private static readonly string[] MetroVancouver = new[]
    {
        "vancouver", "burnaby", "richmond", "surrey", "north vancouver", "west vancouver",
        "coquitlam", "port coquitlam", "port moody", "new westminster", "delta",
        "langley", "white rock", "maple ridge", "pitt meadows", "tsawwassen",
    };

    private static readonly string[] OtherBC = new[]
    {
        "victoria", "kelowna", "kamloops", "whistler", "nanaimo", "abbotsford",
        "chilliwack", "british columbia", " bc,", " bc ", " bc.", "(bc)", "b.c.",
    };

    // Remote tags that imply geographic flexibility we want to keep.
    private static readonly string[] AcceptableRemote = new[]
    {
        "remote, canada", "remote - canada", "remote canada", "remote (canada)",
        "canada remote", "canada (remote)", " canada ",
        "remote, bc", "remote bc",
        "remote, north america", "remote - north america", "remote north america",
        "remote, na", "remote, usa & canada", "us & canada", "us/canada", "u.s. & canada",
        // Bare region tag with no city/country qualifier (e.g. Ashby's plain "North America"
        // location) — same treatment as bare " canada " above: no negative signal, so keep it.
        // Any delimiter: "Remote (North America)" used to pass only via the old bare-"remote" fallback.
        " north america ", " north america)", " north america,", "(north america",
        // "NAMER" = North America (Ashby tenants like Amplitude use "Remote-NAMER"). Delimited
        // forms only, so no ordinary word containing "namer" can match.
        "-namer ", " namer ", "(namer)", "/namer ", ",namer ",
        // "CAN" = Canada (Workday/iCIMS tenants: "Remote CAN"). Delimited so the word "can" in
        // ordinary text doesn't match.
        "remote can ", "remote - can ", "remote, can ", "(can)",
        // Pacific-time remote roles are workable from Vancouver; "Americas" includes Canada.
        " pst ", " pst,", "(pst", "pacific time", "americas",
    };

    // Cities that are clearly NOT Vancouver area. Used to detect "definitely elsewhere"
    // so we drop them even when paired with a remote tag like "Remote, US-East".
    private static readonly string[] OtherCities = new[]
    {
        "new york", "nyc", "ny,", "san francisco", " sf,", "seattle", "los angeles",
        " la,", "boston", "chicago", "austin", "denver", "atlanta", "miami", "dallas",
        "houston", "philadelphia", "san diego", "portland", "phoenix", "minneapolis",
        "washington dc", "washington, d.c.",
        // Other Canadian metros
        "toronto", "montreal", "ottawa", "calgary", "edmonton", "winnipeg", "halifax", "quebec city",
        // International
        "london", "berlin", "paris", "amsterdam", "dublin", "tokyo", "singapore",
        "sydney", "melbourne", "bangalore", "mumbai", "tel aviv",
    };

    // Country labels that mean "not Canada". Many remote jobs use "Remote - United States"
    // or "Remote, U.S." which contain " remote " but no city — the city list alone wouldn't
    // catch them, so we need explicit country-level exclusion. Wrapped in spaces by the
    // " " + ... + " " normalisation in IsVancouverArea so partial-word matches don't fire
    // (e.g. " us " won't match "use" or "bus"). Order: most specific first.
    private static readonly string[] OtherCountriesExclusive = new[]
    {
        "united states", "u.s.a", " u.s.", " usa ", " us,", " us-", " us ", "us only", "us-only",
        "united kingdom", " u.k.", " uk ", " uk,", "uk only", "uk-only",
        "ireland", "germany", "france", "netherlands", "spain", "italy",
        "australia", "new zealand", "india", "japan", "brazil", "mexico",
        "emea", "apac", "latam",
    };

    /// <summary>True if this location should be kept for a Vancouver-area job board.
    /// Blank/null locations return true (we don't have evidence to drop them).</summary>
    public static bool IsVancouverArea(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return true;
        var n = " " + location.ToLowerInvariant() + " ";

        // Strong-positive matches: any Vancouver-area / BC / Canada keyword wins outright, even
        // when paired with a non-Canadian country (e.g. "Remote (US | Canada)" is acceptable).
        if (Contains(n, MetroVancouver)) return true;
        if (Contains(n, OtherBC)) return true;
        if (HasCanadaToken(n)) return true;
        if (Contains(n, AcceptableRemote)) return true;

        // No Canadian signal — reject if we see any non-Canada city or country marker.
        if (Contains(n, OtherCities)) return false;
        if (Contains(n, OtherCountriesExclusive)) return false;

        // Remote with no place attached ("Remote", "Fully remote", "Remote (global)") — accept.
        // Remote *somewhere* we don't recognise ("Remote - DC", "Remote - Colombia", "Illinois
        // Remote Work") — reject. This used to accept anything containing "remote" that missed the
        // city/country lists, which let ~500 US-state and non-Canada remote jobs through.
        return IsPlaceFreeRemote(n);
    }

    // Words that can sit next to "remote" without naming a place. Whatever is left after
    // stripping these (and punctuation) is a place we didn't recognise.
    private static readonly Regex PlaceFreeRemoteNoise = new(
        @"\b(remote|fully|full|100|percent|work|working|from|home|wfh|anywhere|global|globally|worldwide|world|international|flexible|first|friendly|position|role|based|opportunity|or|and|the|in|any|location|locations)\b|[^a-z]+",
        RegexOptions.Compiled);

    /// <summary>True when <paramref name="n"/> says remote and names no place at all.</summary>
    private static bool IsPlaceFreeRemote(string n)
        => n.Contains("remote", StringComparison.Ordinal)
           && PlaceFreeRemoteNoise.Replace(n, "").Length == 0;

    /// <summary>True if "canada" appears as a word (preceded by space, followed by space,
    /// comma, paren, slash, etc. — anything non-letter). Catches "Canada", "Canada,", "Canada)",
    /// "Canada/US" without false-matching imaginary words like "canadagoose".</summary>
    private static bool HasCanadaToken(string n)
    {
        var idx = 0;
        const string needle = " canada";
        while ((idx = n.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            var next = idx + needle.Length;
            if (next >= n.Length || !char.IsLetter(n[next])) return true;
            idx = next;
        }
        return false;
    }

    /// <summary>Heuristic: does the string contain any of the substrings (already lowercased)?</summary>
    private static bool Contains(string haystack, IEnumerable<string> needles)
    {
        foreach (var s in needles)
            if (haystack.Contains(s, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>True if the location is a remote-Canada (or pure-remote) role — the kind
    /// you can do from any city in the configured list, so the city filter should let it through.</summary>
    public static bool IsRemoteAnywhereInCanada(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return false;
        var n = " " + location.ToLowerInvariant() + " ";
        // Pinned cities/countries win over Canada-token. A job tagged "Toronto, Ontario, Canada"
        // is NOT remote-anywhere — it's pinned to Toronto, regardless of the trailing "Canada".
        // Same logic for non-Canadian markers (US-only roles tagged remote).
        if (Contains(n, OtherCities)) return false;
        if (Contains(n, OtherCountriesExclusive)) return false;
        if (HasCanadaToken(n) || Contains(n, AcceptableRemote)) return true;
        return IsPlaceFreeRemote(n);  // "Remote" with no place named
    }
}
