using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jobnet.Services.JobSources;

/// <summary>
/// Extract JobPosting entries from JSON-LD &lt;script type="application/ld+json"&gt; blocks.
/// Many careers pages (especially those built on standard frameworks) embed structured
/// JobPosting schema.org data. This is FAR more reliable than asking an LLM to parse
/// prose, and it's free — no AI call needed.
/// See https://schema.org/JobPosting
/// </summary>
internal static class JsonLdJobExtractor
{
    private static readonly Regex ScriptRe = new(
        @"<script[^>]*type=[""']application/ld\+json[""'][^>]*>(?<body>[\s\S]*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Returns extracted RawJobPosting list (may be empty). Never throws.</summary>
    public static IReadOnlyList<RawJobPosting> Extract(string html, string pageUrl)
    {
        var results = new List<RawJobPosting>();
        if (string.IsNullOrEmpty(html)) return results;

        foreach (Match m in ScriptRe.Matches(html))
        {
            var body = m.Groups["body"].Value.Trim();
            if (body.Length == 0) continue;
            ExtractFromJson(body, pageUrl, results);
        }
        return results;
    }

    private static void ExtractFromJson(string json, string pageUrl, List<RawJobPosting> sink)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            WalkAndCollect(doc.RootElement, pageUrl, sink);
        }
        catch
        {
            // Malformed or partial JSON — silently skip.
        }
    }

    private static void WalkAndCollect(JsonElement el, string pageUrl, List<RawJobPosting> sink)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var c in el.EnumerateArray()) WalkAndCollect(c, pageUrl, sink);
                break;

            case JsonValueKind.Object:
                // A JobPosting object?
                if (IsJobPosting(el))
                {
                    var posting = BuildPosting(el, pageUrl);
                    if (posting is not null) sink.Add(posting);
                }
                // Schema.org @graph wrapper
                if (el.TryGetProperty("@graph", out var graph)) WalkAndCollect(graph, pageUrl, sink);
                // Nested itemListElement on a list of jobs
                if (el.TryGetProperty("itemListElement", out var items)) WalkAndCollect(items, pageUrl, sink);
                break;
        }
    }

    private static bool IsJobPosting(JsonElement obj)
    {
        if (!obj.TryGetProperty("@type", out var t)) return false;
        if (t.ValueKind == JsonValueKind.String) return string.Equals(t.GetString(), "JobPosting", StringComparison.OrdinalIgnoreCase);
        if (t.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in t.EnumerateArray())
                if (v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "JobPosting", StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        return false;
    }

    private static RawJobPosting? BuildPosting(JsonElement obj, string fallbackUrl)
    {
        var title = StrOrNull(obj, "title");
        if (string.IsNullOrWhiteSpace(title)) return null;

        var url = StrOrNull(obj, "url") ?? StrOrNull(obj, "hiringOrganization", "url") ?? fallbackUrl;
        var employmentType = NormalizeEmploymentType(StrOrNull(obj, "employmentType"));
        var department    = StrOrNull(obj, "industry") ?? StrOrNull(obj, "occupationalCategory");
        var description   = StrOrNull(obj, "description");

        // Location can be a string or a Place object with address.addressLocality
        string? location = StrOrNull(obj, "jobLocation", "address", "addressLocality")
                        ?? StrOrNull(obj, "jobLocation", "name")
                        ?? StrOrNull(obj, "applicantLocationRequirements", "name")
                        ?? null;

        var remoteType = (obj.TryGetProperty("jobLocationType", out var jlt) && jlt.ValueKind == JsonValueKind.String
                         && string.Equals(jlt.GetString(), "TELECOMMUTE", StringComparison.OrdinalIgnoreCase))
                         ? "remote" : null;

        var native = StrOrNull(obj, "identifier", "value")
                  ?? StrOrNull(obj, "identifier")
                  ?? url
                  ?? title;

        var (smin, smax, scurrency, speriod) = ExtractSalary(obj);

        return new RawJobPosting
        {
            NativeId = ShortHash(native),
            Title = title!,
            Url = url,
            Location = location,
            RemoteType = remoteType,
            EmploymentType = employmentType,
            Department = department,
            SalaryMin = smin,
            SalaryMax = smax,
            SalaryCurrency = scurrency,
            SalaryPeriod = speriod,
            DescriptionSnippet = SnippetCleaner.Clean(description, maxChars: 500),
        };
    }

    /// <summary>Parse schema.org baseSalary. Shape commonly:
    /// { "@type": "MonetaryAmount", "currency": "USD",
    ///   "value": { "@type": "QuantitativeValue", "minValue": 80000, "maxValue": 120000, "unitText": "YEAR" } }
    /// </summary>
    private static (int? Min, int? Max, string? Currency, string? Period) ExtractSalary(JsonElement obj)
    {
        if (!obj.TryGetProperty("baseSalary", out var bs)) return (null, null, null, null);

        if (bs.ValueKind == JsonValueKind.Number && bs.TryGetInt32(out var single))
            return (single, single, null, null);

        if (bs.ValueKind != JsonValueKind.Object) return (null, null, null, null);

        string? currency = StrOrNull(bs, "currency");
        if (!bs.TryGetProperty("value", out var v)) return (null, null, currency, null);

        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var asSingle))
            return (asSingle, asSingle, currency, null);

        if (v.ValueKind != JsonValueKind.Object) return (null, null, currency, null);

        int? min = TryReadInt(v, "minValue") ?? TryReadInt(v, "value");
        int? max = TryReadInt(v, "maxValue") ?? min;
        string? period = (StrOrNull(v, "unitText") ?? "").ToUpperInvariant() switch
        {
            "YEAR"  => "year",
            "MONTH" => "month",
            "HOUR"  => "hour",
            _ => null,
        };
        return (min, max, currency, period);
    }

    private static int? TryReadInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i)) return i;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d)) return (int)Math.Round(d);
        if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out var s)) return s;
        return null;
    }

    private static string? StrOrNull(JsonElement obj, params string[] path)
    {
        JsonElement cur = obj;
        foreach (var step in path)
        {
            if (cur.ValueKind != JsonValueKind.Object) return null;
            if (!cur.TryGetProperty(step, out var next)) return null;
            cur = next;
        }
        return cur.ValueKind switch
        {
            JsonValueKind.String => cur.GetString(),
            JsonValueKind.Number => cur.GetRawText(),
            _ => null,
        };
    }

    private static string? NormalizeEmploymentType(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var v = raw.ToUpperInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
        if (v.Contains("FULLTIME")) return "full-time";
        if (v.Contains("PARTTIME")) return "part-time";
        if (v.Contains("CONTRACT") || v.Contains("TEMP") || v.Contains("FREELANCE")) return "contract";
        return null;
    }

    private static string ShortHash(string s)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).Substring(0, 16).ToLowerInvariant();
    }
}
