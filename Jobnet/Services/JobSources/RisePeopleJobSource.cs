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

/// <summary>
/// Rise People ATS — Canadian SMB HRIS hosted at <c>careers.risepeople.com/{slug}/...</c>.
///
/// API: <c>https://gateway.risepeople.com/applicant_tracking/public/careers?company_uri={slug}&amp;language=en</c>
///
/// Discovery story: the SPA's main.js bundle has an <c>Authorization: "Basic " + btoa(...)</c>
/// code path that initially looked like a blocker. A Playwright HAR capture of two live boards
/// (Foresight Cleantech and Settle Smart) proved that path is for *applicant submission*, NOT
/// for the public job-listing read. The careers endpoint is anonymously accessible — works
/// even with the <c>Origin</c> header stripped.
///
/// Response shape (confirmed against the live <c>rise</c> board, 2026-09-26):
/// <code>
/// { "settings": { ... },
///   "departments": [
///     { "title": "R&amp;D - Product",
///       "positions": [
///         { "id": 16402, "title": "...", "city": "Vancouver", "province": "BC",
///           "country": "Canada", "remote_position": true, "job_type": ["FULL_TIME"] } ] } ] }
/// </code>
/// The list carries no URL; the SPA's posting route is <c>/{company_uri}/{lang}/{position id}</c>.
/// Fields are still read via <see cref="JsonElement"/> so an unexpected shape yields nulls
/// rather than failing the batch.
///
/// Slug = the <c>company_uri</c> segment from the URL (e.g. <c>foresightcanada</c>,
/// <c>settle-smart-technologies-sb</c>).
/// </summary>
public sealed class RisePeopleJobSource : IJobSource
{
    public const string Provider = "ats_risepeople";
    public string AtsType => "risepeople";

    private readonly HttpClient _http;
    private readonly IApiUsageTracker _usage;
    private readonly IRateLimiter _rateLimiter;

    public RisePeopleJobSource(HttpClient http, IApiUsageTracker usage, IRateLimiter rateLimiter)
    {
        _http = http;
        _usage = usage;
        _rateLimiter = rateLimiter;
    }

    public async Task<IReadOnlyList<RawJobPosting>> FetchAsync(string slug, CancellationToken ct = default)
    {
        var url = "https://gateway.risepeople.com/applicant_tracking/public/careers"
                + $"?company_uri={Uri.EscapeDataString(slug)}&language=en";

        await _rateLimiter.WaitAsync(Provider, ct);
        _usage.RecordCall(Provider);

        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Rise People HTTP {(int)resp.StatusCode} for slug '{slug}'",
                                            null, resp.StatusCode);

        var payload = await resp.Content.ReadFromJsonAsync<Response>(cancellationToken: ct);
        var depts = payload?.Departments ?? new();

        var results = new List<RawJobPosting>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in depts)
        {
            var positions = d.Positions ?? d.Postings;
            if (positions is null) continue;
            var deptName = !string.IsNullOrWhiteSpace(d.Title) ? d.Title
                         : !string.IsNullOrWhiteSpace(d.Name) ? d.Name : null;
            foreach (var p in positions)
            {
                var id = ReadString(p, "id") ?? ReadString(p, "uuid");
                var title = ReadString(p, "title") ?? ReadString(p, "name");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title)) continue;
                if (!seen.Add(id)) continue;   // dedupe across departments

                var locParts = new[] { ReadString(p, "city"), ReadString(p, "province"), ReadString(p, "country") }
                    .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                var location = locParts.Count > 0 ? string.Join(", ", locParts) : ReadString(p, "location");
                var isRemote = ReadBool(p, "remote_position");
                var employment = ReadFirstString(p, "job_type") ?? ReadString(p, "employment_type");

                results.Add(new RawJobPosting
                {
                    NativeId = id!,
                    Title = title!.Trim(),
                    Url = $"https://careers.risepeople.com/{slug}/en/{Uri.EscapeDataString(id!)}",
                    Location = location,
                    RemoteType = isRemote == true ? "remote" : NormalizeRemote(null, location),
                    EmploymentType = NormalizeEmployment(employment),
                    Department = deptName,
                    DescriptionSnippet = null,   // not in the list payload
                });
            }
        }
        return results;
    }

    /// <summary>Pull a string field from a posting object by name, tolerating both
    /// camelCase and snake_case (Rise's JSON uses snake_case consistently but the bundle
    /// passed through camelCase variants in some code paths — we accept either).</summary>
    private static string? ReadString(JsonElement? obj, string name)
    {
        if (obj is not { ValueKind: JsonValueKind.Object } e) return null;
        if (TryGet(e, name, out var v) ||
            TryGet(e, ToCamel(name), out v))
        {
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
        }
        return null;

        static bool TryGet(JsonElement e, string n, out JsonElement v)
        {
            if (e.TryGetProperty(n, out v)) return true;
            v = default;
            return false;
        }
        static string ToCamel(string snake)
        {
            var parts = snake.Split('_');
            if (parts.Length == 1) return snake;
            return parts[0] + string.Concat(parts.Skip(1).Select(p =>
                p.Length == 0 ? "" : char.ToUpperInvariant(p[0]) + p.Substring(1)));
        }
    }

    private static bool? ReadBool(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v)
            && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    /// <summary>First string of an array field (Rise's <c>job_type</c> is e.g. <c>["FULL_TIME"]</c>),
    /// or the field itself if it's a plain string.</summary>
    private static string? ReadFirstString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        if (v.ValueKind == JsonValueKind.Array)
            foreach (var e in v.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String) return e.GetString();
        return null;
    }

    private static string NormalizeRemote(string? remoteType, string? location)
    {
        var hay = ((remoteType ?? "") + " " + (location ?? "")).ToLowerInvariant();
        if (hay.Contains("remote"))                                  return "remote";
        if (hay.Contains("hybrid"))                                  return "hybrid";
        if (hay.Contains("onsite") || hay.Contains("on-site") || hay.Contains("on_site")) return "on-site";
        return "unknown";
    }

    private static string NormalizeEmployment(string? employmentType)
    {
        if (string.IsNullOrEmpty(employmentType)) return "unknown";
        var v = employmentType.ToLowerInvariant();
        if (v.Contains("full"))                          return "full-time";
        if (v.Contains("part"))                          return "part-time";
        if (v.Contains("contract") || v.Contains("temp") || v.Contains("seasonal")) return "contract";
        return "unknown";
    }

    public sealed class Response
    {
        [JsonPropertyName("settings")]    public JsonElement? Settings { get; set; }
        [JsonPropertyName("departments")] public List<Department>? Departments { get; set; }
    }

    public sealed class Department
    {
        [JsonPropertyName("title")]     public string? Title { get; set; }
        [JsonPropertyName("name")]      public string? Name { get; set; }
        [JsonPropertyName("positions")] public List<JsonElement>? Positions { get; set; }
        [JsonPropertyName("postings")]  public List<JsonElement>? Postings { get; set; }
    }
}
