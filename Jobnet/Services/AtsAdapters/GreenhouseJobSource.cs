using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Services.ApiUsage;
using Jobnet.Services.RateLimit;

namespace Jobnet.Services.AtsAdapters;

public sealed class GreenhouseJobSource : IAtsJobSource
{
    public const string Provider = "ats_greenhouse";
    public string AtsType => "greenhouse";

    private readonly HttpClient _http;
    private readonly IApiUsageTracker _usage;
    private readonly IRateLimiter _rateLimiter;

    public GreenhouseJobSource(HttpClient http, IApiUsageTracker usage, IRateLimiter rateLimiter)
    {
        _http = http;
        _usage = usage;
        _rateLimiter = rateLimiter;
    }

    public async Task<IReadOnlyList<RawJobPosting>> FetchAsync(string slug, CancellationToken ct = default)
    {
        var url = $"https://boards-api.greenhouse.io/v1/boards/{Uri.EscapeDataString(slug)}/jobs?content=true";

        await _rateLimiter.WaitAsync(Provider, ct);
        _usage.RecordCall(Provider);

        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Greenhouse HTTP {(int)resp.StatusCode}");
        var payload = await resp.Content.ReadFromJsonAsync<Response>(cancellationToken: ct);
        var items = payload?.Jobs ?? new();
        var results = new List<RawJobPosting>(items.Count);
        foreach (var j in items)
        {
            if (j.Id is null || string.IsNullOrEmpty(j.Title)) continue;
            var (sMin, sMax, sCur) = ExtractSalary(j);
            results.Add(new RawJobPosting
            {
                NativeId = j.Id.Value.ToString(),
                Title = j.Title!,
                Url = j.AbsoluteUrl,
                Location = j.Location?.Name,
                RemoteType = GuessRemoteType(j.Location?.Name),
                EmploymentType = "full-time",
                Department = (j.Departments != null && j.Departments.Count > 0) ? j.Departments[0].Name : null,
                DescriptionSnippet = SnippetCleaner.Clean(j.Content, maxChars: 500),
                SalaryMin = sMin,
                SalaryMax = sMax,
                SalaryCurrency = sCur,
                SalaryPeriod = (sMin.HasValue || sMax.HasValue) ? "year" : null,
            });
        }
        return results;
    }

    /// <summary>Pull salary out of a Greenhouse posting. Two conventions seen in the wild:
    /// (a) top-level <c>pay_input_ranges</c> (newer); (b) custom metadata entries with
    /// <c>value_type=="currency_range"</c> and a name containing "salary" / "pay" / "compensation"
    /// (used by Block and many others — zoned by region). We prefer CAD entries since the app
    /// is Vancouver-focused; fall back to USD when no Canadian range exists. Returns annual values.</summary>
    private static (int? Min, int? Max, string? Currency) ExtractSalary(Posting j)
    {
        // Path A: top-level pay_input_ranges.
        if (j.PayInputRanges is { Count: > 0 })
        {
            var pick = j.PayInputRanges.FirstOrDefault(r => string.Equals(r.Currency, "CAD", StringComparison.OrdinalIgnoreCase))
                     ?? j.PayInputRanges.OrderByDescending(r => r.MaxCents ?? r.MinCents ?? 0).FirstOrDefault();
            if (pick is not null && (pick.MinCents.HasValue || pick.MaxCents.HasValue))
            {
                // pay_input_ranges values are in cents per year.
                int? min = pick.MinCents.HasValue ? (int?)(pick.MinCents.Value / 100L) : null;
                int? max = pick.MaxCents.HasValue ? (int?)(pick.MaxCents.Value / 100L) : null;
                return (min, max, pick.Currency?.ToUpperInvariant());
            }
        }

        // Path B: scan metadata for currency_range entries with a pay-ish name. Pick CAD if any,
        // else USD with the highest max (typically the senior-tier zone).
        if (j.Metadata is { Count: > 0 })
        {
            var pays = new List<(int Min, int Max, string Currency, string Name)>();
            foreach (var m in j.Metadata)
            {
                if (!string.Equals(m.ValueType, "currency_range", StringComparison.OrdinalIgnoreCase)) continue;
                var name = m.Name ?? "";
                if (!(name.Contains("salary", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("pay range", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("pay band", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("compensation", StringComparison.OrdinalIgnoreCase))) continue;
                if (m.Value.ValueKind != JsonValueKind.Object) continue;
                try
                {
                    var unit = m.Value.GetProperty("unit").GetString() ?? "";
                    var min  = (int)Math.Round(m.Value.GetProperty("min_value").GetDouble());
                    var max  = (int)Math.Round(m.Value.GetProperty("max_value").GetDouble());
                    if (max <= 0) continue;
                    pays.Add((min, max, unit.ToUpperInvariant(), name));
                }
                catch { /* malformed entry — skip */ }
            }
            if (pays.Count > 0)
            {
                var cad = pays.Where(p => p.Currency == "CAD").OrderByDescending(p => p.Max).FirstOrDefault();
                if (cad.Max > 0) return (cad.Min, cad.Max, "CAD");
                var best = pays.OrderByDescending(p => p.Max).First();
                return (best.Min, best.Max, best.Currency);
            }
        }
        return (null, null, null);
    }

    private static string? StripTags(string html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        var s = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        s = System.Net.WebUtility.HtmlDecode(s);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    private static string GuessRemoteType(string? location)
    {
        if (string.IsNullOrEmpty(location)) return "unknown";
        var l = location.ToLowerInvariant();
        if (l.Contains("remote")) return "remote";
        if (l.Contains("hybrid")) return "hybrid";
        return "on-site";
    }

    private sealed class Response
    {
        [JsonPropertyName("jobs")] public List<Posting>? Jobs { get; set; }
    }

    private sealed class Posting
    {
        [JsonPropertyName("id")]                public long? Id { get; set; }
        [JsonPropertyName("title")]             public string? Title { get; set; }
        [JsonPropertyName("absolute_url")]      public string? AbsoluteUrl { get; set; }
        [JsonPropertyName("location")]          public LocationObj? Location { get; set; }
        [JsonPropertyName("departments")]       public List<NamedObj>? Departments { get; set; }
        [JsonPropertyName("content")]           public string? Content { get; set; }
        [JsonPropertyName("metadata")]          public List<MetadataEntry>? Metadata { get; set; }
        [JsonPropertyName("pay_input_ranges")]  public List<PayRange>? PayInputRanges { get; set; }
    }

    private sealed class LocationObj
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private sealed class NamedObj
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private sealed class MetadataEntry
    {
        [JsonPropertyName("name")]       public string? Name { get; set; }
        [JsonPropertyName("value_type")] public string? ValueType { get; set; }
        // Polymorphic — string for "single_select", object for "currency_range", etc.
        [JsonPropertyName("value")]      public JsonElement Value { get; set; }
    }

    /// <summary>Newer top-level <c>pay_input_ranges</c> field. min_cents/max_cents are in the
    /// smallest currency unit. currency_type is ISO-4217 (e.g. "USD", "CAD").</summary>
    private sealed class PayRange
    {
        [JsonPropertyName("min_cents")]     public long? MinCents { get; set; }
        [JsonPropertyName("max_cents")]     public long? MaxCents { get; set; }
        [JsonPropertyName("currency_type")] public string? Currency { get; set; }
    }
}
