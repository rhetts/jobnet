using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Services.ApiUsage;
using Jobnet.Services.RateLimit;

namespace Jobnet.Services.JobSources;

/// <summary>
/// Recruitee's public careers-site JSON API. Every customer gets a subdomain like
/// <c>guarana.recruitee.com</c>, and that subdomain serves a public offers list at
/// <c>/api/offers/</c> — same shape as the widget endpoint some companies embed on their own
/// domain instead (<c>career.recruitee.com/api/c/{numeric_id}/widget</c>), but keyed by the
/// stable company subdomain rather than an opaque numeric id, so that's what <c>ats_slug</c>
/// stores. Confirmed directly: <c>https://guarana.recruitee.com/api/offers/</c> returns the same
/// offers as the widget call the AI-extract path had been paying for.
///
/// Not every field below is used yet (salary, description) but they're cheap to carry since the
/// API already returns them — Recruitee is unusual among the ATSs this app talks to in exposing
/// full salary + description on the list endpoint itself, no per-job follow-up call needed.
/// </summary>
public sealed class RecruiteeJobSource : IJobSource
{
    public const string Provider = "ats_recruitee";
    public string AtsType => "recruitee";

    private readonly HttpClient _http;
    private readonly IApiUsageTracker _usage;
    private readonly IRateLimiter _rateLimiter;

    public RecruiteeJobSource(HttpClient http, IApiUsageTracker usage, IRateLimiter rateLimiter)
    {
        _http = http;
        _usage = usage;
        _rateLimiter = rateLimiter;
    }

    public async Task<IReadOnlyList<RawJobPosting>> FetchAsync(string slug, CancellationToken ct = default)
    {
        // The slug is the subdomain — e.g. "guarana" -> guarana.recruitee.com.
        var url = $"https://{Uri.EscapeDataString(slug)}.recruitee.com/api/offers/";

        await _rateLimiter.WaitAsync(Provider, ct);
        _usage.RecordCall(Provider);

        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Recruitee HTTP {(int)resp.StatusCode} for slug '{slug}'");

        var payload = await resp.Content.ReadFromJsonAsync<Response>(cancellationToken: ct);
        return ParseResponse(payload);
    }

    /// <summary>Pulled out for unit testing — accepts an already-deserialised <see cref="Response"/>
    /// (captured JSON fixture) and returns the same shape FetchAsync would.</summary>
    public static IReadOnlyList<RawJobPosting> ParseResponse(Response? payload)
    {
        var offers = payload?.Offers ?? new();
        var results = new List<RawJobPosting>(offers.Count);
        foreach (var o in offers)
        {
            if (o.Id is null || string.IsNullOrWhiteSpace(o.Title)) continue;

            results.Add(new RawJobPosting
            {
                NativeId = o.Id.Value.ToString(),
                Title = o.Title!,
                Url = o.CareersUrl,
                Location = FormatLocation(o),
                RemoteType = ResolveRemoteType(o),
                EmploymentType = NormalizeEmployment(o.EmploymentTypeCode),
                Department = o.Department,
                DescriptionSnippet = SnippetCleaner.Clean(o.Requirements ?? o.Description, maxChars: 500),
                SalaryMin = ParseSalaryAmount(o.Salary?.Min),
                SalaryMax = ParseSalaryAmount(o.Salary?.Max),
                SalaryCurrency = o.Salary?.Currency,
                SalaryPeriod = NormalizePeriod(o.Salary?.Period),
            });
        }
        return results;
    }

    private static string? FormatLocation(Offer o)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(o.City)) parts.Add(o.City!);
        if (!string.IsNullOrWhiteSpace(o.StateName)) parts.Add(o.StateName!);
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>Recruitee exposes remote/hybrid/on-site as three independent booleans (a posting
    /// can allow more than one) rather than a single enum — prefer the most flexible signal when
    /// several are set, since that's the strongest single word for what the posting actually
    /// allows: pure remote first, then hybrid, then on-site.</summary>
    private static string ResolveRemoteType(Offer o)
    {
        if (o.Remote == true && o.Hybrid != true && o.OnSite != true) return "remote";
        if (o.Hybrid == true) return "hybrid";
        if (o.OnSite == true) return "on-site";
        if (o.Remote == true) return "remote";
        return "unknown";
    }

    private static string NormalizeEmployment(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "unknown";
        var c = code.ToLowerInvariant();
        if (c.Contains("fulltime")) return "full-time";
        if (c.Contains("parttime")) return "part-time";
        if (c.Contains("temporary") || c.Contains("intern") || c.Contains("apprentice")) return "contract";
        return "unknown";
    }

    private static string? NormalizePeriod(string? period)
    {
        if (string.IsNullOrWhiteSpace(period)) return null;
        var p = period.Trim().ToLowerInvariant();
        return p is "year" or "month" or "hour" ? p : null;
    }

    private static int? ParseSalaryAmount(string? raw) =>
        int.TryParse(raw, out var n) ? n : null;

    public sealed class Response
    {
        [JsonPropertyName("offers")] public List<Offer>? Offers { get; set; }
    }

    public sealed class Offer
    {
        [JsonPropertyName("id")]                  public long? Id { get; set; }
        [JsonPropertyName("title")]                public string? Title { get; set; }
        [JsonPropertyName("careers_url")]          public string? CareersUrl { get; set; }
        [JsonPropertyName("department")]           public string? Department { get; set; }
        [JsonPropertyName("city")]                 public string? City { get; set; }
        [JsonPropertyName("state_name")]           public string? StateName { get; set; }
        [JsonPropertyName("remote")]                public bool? Remote { get; set; }
        [JsonPropertyName("hybrid")]                public bool? Hybrid { get; set; }
        [JsonPropertyName("on_site")]               public bool? OnSite { get; set; }
        [JsonPropertyName("employment_type_code")]  public string? EmploymentTypeCode { get; set; }
        [JsonPropertyName("description")]           public string? Description { get; set; }
        [JsonPropertyName("requirements")]          public string? Requirements { get; set; }
        [JsonPropertyName("salary")]                public SalaryObj? Salary { get; set; }
    }

    public sealed class SalaryObj
    {
        [JsonPropertyName("min")]      public string? Min { get; set; }
        [JsonPropertyName("max")]      public string? Max { get; set; }
        [JsonPropertyName("period")]   public string? Period { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
    }
}
