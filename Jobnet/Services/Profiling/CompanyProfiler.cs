using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Jobnet.Data;
using Jobnet.Models;
using Jobnet.Services.ApiUsage;
using Jobnet.Services.Claude;
using Jobnet.Services.RateLimit;

namespace Jobnet.Services.Profiling;

public sealed class CompanyProfiler : ICompanyProfiler
{
    public const string HttpProvider = "http_fetch";

    private readonly HttpClient _http;
    private readonly IClaudeClient _claude;
    private readonly IApiUsageTracker _usage;
    private readonly IRateLimiter _rateLimiter;
    private readonly IDbConnectionFactory _connections;

    public CompanyProfiler(HttpClient http, IClaudeClient claude, IApiUsageTracker usage,
                            IRateLimiter rateLimiter, IDbConnectionFactory connections)
    {
        _http = http;
        _claude = claude;
        _usage = usage;
        _rateLimiter = rateLimiter;
        _connections = connections;
    }

    public async Task<ProfileResult> GenerateAndPersistAsync(Company company, CancellationToken ct = default)
    {
        if (!_claude.IsConfigured)
            return new ProfileResult { Success = false, Error = "Claude API key not configured" };

        // Fetch homepage and /about page, concatenate text.
        var pages = new List<(string Url, string Text)>();
        foreach (var url in CandidateUrls(company))
        {
            var text = await FetchTextAsync(url, ct);
            if (!string.IsNullOrEmpty(text)) pages.Add((url, text));
            if (pages.Count >= 2) break; // homepage + /about is enough
        }
        if (pages.Count == 0)
            return new ProfileResult { Success = false, Error = "No content fetched from candidate URLs" };

        var combinedText = string.Join("\n\n--- next page ---\n\n",
            pages.Select(p => $"[{p.Url}]\n{p.Text}"));
        if (combinedText.Length > 8000) combinedText = combinedText.Substring(0, 8000);

        var system =
            "You analyze company websites and produce a strict-JSON profile. " +
            "No prose, no commentary, no code fences — just JSON.";
        var user =
            "Schema:\n" +
            "{\n" +
            "  \"summary\": \"1-2 sentence description of what the company does and sells\",\n" +
            "  \"products\": [\"top product/service name\", ...],\n" +
            "  \"industries\": [\"industry tag\", ...],\n" +
            "  \"tech_signals\": [\"named tech stack, framework, or notable signal\", ...],\n" +
            "  \"hq_hint\": \"city/region if you can infer\",\n" +
            "  \"size_hint\": \"employee count range if mentioned, e.g. '50-200'\"\n" +
            "}\n" +
            "Keep arrays ≤5 entries. Use empty arrays/null when nothing relevant.\n\n" +
            "Company: " + (company.Name ?? company.Domain) + " (" + company.Domain + ")\n\n" +
            "Page content:\n" + combinedText;

        ClaudeResponse response;
        try
        {
            response = await _claude.CompleteAsync(user, system, maxTokens: 1024, ct);
        }
        catch (Exception ex)
        {
            return new ProfileResult { Success = false, Error = $"Claude call failed: {ex.Message}" };
        }

        var profile = ParseProfile(response.Text, response.Model);
        if (profile is null)
            return new ProfileResult { Success = false, Error = $"Could not parse profile JSON. Raw: {Truncate(response.Text, 200)}" };

        Persist(company.Id, profile);
        return new ProfileResult { Profile = profile, Success = true, SourceUrl = pages[0].Url };
    }

    private async Task<string> FetchTextAsync(string url, CancellationToken ct)
    {
        try
        {
            await _rateLimiter.WaitAsync(HttpProvider, ct);
            _usage.RecordCall(HttpProvider);
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return "";
            var html = await resp.Content.ReadAsStringAsync(ct);
            return HtmlTextExtractor.Extract(html, maxChars: 6000);
        }
        catch
        {
            return "";
        }
    }

    private static IEnumerable<string> CandidateUrls(Company company)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? primary = null;
        if (!string.IsNullOrWhiteSpace(company.WebsiteUrl)) primary = company.WebsiteUrl.TrimEnd('/');
        primary ??= $"https://{company.Domain}";
        if (seen.Add(primary)) yield return primary;

        var about = primary + "/about";
        if (seen.Add(about)) yield return about;

        var aboutUs = primary + "/about-us";
        if (seen.Add(aboutUs)) yield return aboutUs;

        var company_page = primary + "/company";
        if (seen.Add(company_page)) yield return company_page;
    }

    private static CompanyProfile? ParseProfile(string responseText, string model)
    {
        var json = StripFences(responseText.Trim());
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new CompanyProfile
            {
                Summary = StringOrNull(root, "summary"),
                Products = ArrayOrEmpty(root, "products"),
                Industries = ArrayOrEmpty(root, "industries"),
                TechSignals = ArrayOrEmpty(root, "tech_signals"),
                HeadquartersHint = StringOrNull(root, "hq_hint"),
                SizeHint = StringOrNull(root, "size_hint"),
                GeneratedAt = DateTime.UtcNow,
                Model = model,
            };
        }
        catch
        {
            return null;
        }
    }

    private void Persist(int companyId, CompanyProfile p)
    {
        using var conn = _connections.Open();
        conn.Execute(@"
            UPDATE companies SET
                profile_summary      = @Summary,
                profile_products     = @Products,
                profile_industries   = @Industries,
                profile_tech_signals = @Signals,
                profile_hq_hint      = @Hq,
                profile_size_hint    = @Size,
                profile_generated_at = @GeneratedAt,
                profile_model        = @Model
            WHERE id = @Id",
            new
            {
                Id = companyId,
                p.Summary,
                Products = JsonSerializer.Serialize(p.Products),
                Industries = JsonSerializer.Serialize(p.Industries),
                Signals = JsonSerializer.Serialize(p.TechSignals),
                Hq = p.HeadquartersHint,
                Size = p.SizeHint,
                GeneratedAt = p.GeneratedAt?.ToString("o"),
                p.Model,
            });
    }

    private static string? StringOrNull(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<string> ArrayOrEmpty(JsonElement root, string name)
    {
        var list = new List<string>();
        if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var elt in arr.EnumerateArray())
            if (elt.ValueKind == JsonValueKind.String) list.Add(elt.GetString() ?? "");
        return list;
    }

    private static string StripFences(string s)
    {
        if (s.StartsWith("```"))
        {
            var firstNl = s.IndexOf('\n');
            if (firstNl > 0) s = s[(firstNl + 1)..];
            if (s.EndsWith("```")) s = s[..^3];
        }
        return s.Trim();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
}
