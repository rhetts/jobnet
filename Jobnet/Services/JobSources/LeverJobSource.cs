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

namespace Jobnet.Services.JobSources;

public sealed class LeverJobSource : IJobSource
{
    public const string Provider = "ats_lever";
    public string AtsType => "lever";

    private readonly HttpClient _http;
    private readonly IApiUsageTracker _usage;
    private readonly IRateLimiter _rateLimiter;

    public LeverJobSource(HttpClient http, IApiUsageTracker usage, IRateLimiter rateLimiter)
    {
        _http = http;
        _usage = usage;
        _rateLimiter = rateLimiter;
    }

    public async Task<IReadOnlyList<RawJobPosting>> FetchAsync(string slug, CancellationToken ct = default)
    {
        var url = $"https://api.lever.co/v0/postings/{Uri.EscapeDataString(slug)}?mode=json";

        await _rateLimiter.WaitAsync(Provider, ct);
        _usage.RecordCall(Provider);

        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Lever HTTP {(int)resp.StatusCode}");
        var items = await resp.Content.ReadFromJsonAsync<List<Posting>>(cancellationToken: ct) ?? new();
        var results = new List<RawJobPosting>(items.Count);
        foreach (var j in items)
        {
            if (string.IsNullOrEmpty(j.Id) || string.IsNullOrEmpty(j.Text)) continue;
            results.Add(new RawJobPosting
            {
                NativeId = j.Id!,
                Title = j.Text!,
                Url = j.HostedUrl ?? j.ApplyUrl,
                Location = j.Categories?.Location,
                RemoteType = GuessRemoteType(j.Categories?.Location, j.WorkplaceType),
                EmploymentType = j.Categories?.Commitment?.ToLowerInvariant(),
                Department = j.Categories?.Department ?? j.Categories?.Team,
                DescriptionSnippet = SnippetCleaner.Clean(j.DescriptionPlain, maxChars: 500),
                SalaryMin = ReadIntLoose(j.SalaryRange?.Min),
                SalaryMax = ReadIntLoose(j.SalaryRange?.Max),
                SalaryCurrency = j.SalaryRange?.Currency,
                SalaryPeriod = NormalizeInterval(j.SalaryRange?.Interval),
            });
        }
        return results;
    }

    private static string GuessRemoteType(string? location, string? workplaceType)
    {
        if (!string.IsNullOrEmpty(workplaceType))
        {
            var w = workplaceType.ToLowerInvariant();
            if (w.Contains("remote")) return "remote";
            if (w.Contains("hybrid")) return "hybrid";
            if (w.Contains("on-site") || w.Contains("onsite")) return "on-site";
        }
        if (!string.IsNullOrEmpty(location))
        {
            var l = location.ToLowerInvariant();
            if (l.Contains("remote")) return "remote";
            if (l.Contains("hybrid")) return "hybrid";
        }
        return "unknown";
    }

    private static string? Trunc(string? s, int n) => string.IsNullOrEmpty(s) ? null : (s.Length <= n ? s : s.Substring(0, n));

    private static string? NormalizeInterval(string? v)
    {
        if (string.IsNullOrEmpty(v)) return null;
        var l = v.ToLowerInvariant();
        if (l.Contains("year") || l == "annual") return "year";
        if (l.Contains("month")) return "month";
        if (l.Contains("hour")) return "hour";
        return null;
    }

    private sealed class Posting
    {
        [JsonPropertyName("id")]              public string? Id { get; set; }
        [JsonPropertyName("text")]            public string? Text { get; set; }
        [JsonPropertyName("hostedUrl")]       public string? HostedUrl { get; set; }
        [JsonPropertyName("applyUrl")]        public string? ApplyUrl { get; set; }
        [JsonPropertyName("workplaceType")]   public string? WorkplaceType { get; set; }
        [JsonPropertyName("descriptionPlain")] public string? DescriptionPlain { get; set; }
        [JsonPropertyName("categories")]      public Categories? Categories { get; set; }
        [JsonPropertyName("salaryRange")]     public SalaryRangeObj? SalaryRange { get; set; }
    }

    private sealed class Categories
    {
        [JsonPropertyName("commitment")] public string? Commitment { get; set; }
        [JsonPropertyName("location")]   public string? Location { get; set; }
        [JsonPropertyName("team")]       public string? Team { get; set; }
        [JsonPropertyName("department")] public string? Department { get; set; }
    }

    private sealed class SalaryRangeObj
    {
        // Lever returns salaryRange.min/max as either an integer ("120000"), a quoted string
        // ("$120,000"), or a float (120000.0) depending on which Match Group brand owns the
        // posting. A strict int? mapping fails the entire batch on the first non-int row — that's
        // what was killing the POF refresh on $[20].salaryRange.min. Use JsonElement so the
        // deserialiser accepts anything, and convert ourselves with <see cref="ReadIntLoose"/>.
        [JsonPropertyName("min")]      public JsonElement? Min { get; set; }
        [JsonPropertyName("max")]      public JsonElement? Max { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("interval")] public string? Interval { get; set; }
    }

    /// <summary>Best-effort int extraction from a JsonElement that could be Number, String, or
    /// Null. Used for Lever's salaryRange.min/max where the value type varies by tenant. Returns
    /// null on anything we can't coerce so a single oddball row doesn't break the batch.</summary>
    private static int? ReadIntLoose(JsonElement? elt)
    {
        if (elt is not { } e) return null;
        switch (e.ValueKind)
        {
            case JsonValueKind.Number:
                if (e.TryGetInt32(out var n))   return n;
                if (e.TryGetDouble(out var dn)) return (int)Math.Round(dn);
                return null;
            case JsonValueKind.String:
                var s = e.GetString();
                if (string.IsNullOrEmpty(s)) return null;
                // Strip currency symbols and grouping separators ("$120,000" → "120000").
                var cleaned = new string(s.Where(c => char.IsDigit(c) || c == '.' || c == '-').ToArray());
                if (double.TryParse(cleaned, System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out var ds))
                    return (int)Math.Round(ds);
                return null;
            default:
                return null;
        }
    }
}
