using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Jobnet.Services.Ai;

namespace Jobnet.Services.Discovery.Strategies;

/// <summary>
/// For each company in the DB that has at least one active job (so we know it's real and
/// hiring), ask the AI to name 5 Vancouver / Canadian tech companies in adjacent spaces.
/// Catches lesser-known firms that don't rank for our keyword searches but DO compete with
/// firms we already know — e.g. fintech competitors of Trulioo, devtools peers of 1Password.
/// </summary>
public sealed class AiCompetitorStrategy : IDiscoveryStrategy
{
    private readonly IAiClient _ai;
    private readonly ICompanyRepository _companies;
    private readonly IJobRepository _jobs;
    private readonly ICompanyDiscoveryRepository _sightings;

    public string Name => "AI: suggest competitors of known companies";
    public string Description => "For each company with active jobs, ask Gemini to name 5 similar Canadian tech firms. " +
                                  "Cheap and high-yield for lesser-known firms our keyword search misses.";

    public AiCompetitorStrategy(IAiClient ai, ICompanyRepository companies, IJobRepository jobs,
                                  ICompanyDiscoveryRepository sightings)
    {
        _ai = ai;
        _companies = companies;
        _jobs = jobs;
        _sightings = sightings;
    }

    public async Task<StrategyReport> RunAsync(CancellationToken ct = default)
    {
        var report = new StrategyReport();
        if (!_ai.IsConfigured)
        {
            report.Errors.Add("AI provider not configured.");
            return report;
        }

        var activeCounts = _jobs.GetActiveCountsByCompany();
        var seeds = _companies.GetAll()
            .Where(c => activeCounts.ContainsKey(c.Id))
            .ToList();

        var existing = _companies.GetAll().Select(c => c.Domain)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var domainsThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seedDomains = new HashSet<string>(seeds.Select(s => s.Domain), StringComparer.OrdinalIgnoreCase);

        foreach (var seed in seeds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var suggestions = await SuggestForAsync(seed, ct);
                report.CandidatesExamined += suggestions.Count;
                if (suggestions.Count == 0)
                    report.Errors.Add($"[{seed.Name}] AI returned no suggestions");

                foreach (var s in suggestions)
                {
                    if (string.IsNullOrWhiteSpace(s.Name) || string.IsNullOrWhiteSpace(s.Website))
                    {
                        report.CompaniesSkippedFiltered++;
                        continue;
                    }
                    var domain = CanonicalDomain(s.Website);
                    if (string.IsNullOrEmpty(domain) || IsBlockedDomain(domain) || seedDomains.Contains(domain))
                    {
                        report.CompaniesSkippedFiltered++;
                        continue;
                    }
                    if (existing.Contains(domain))
                    {
                        // Existing — record this AI sighting too so we can see "Trulioo also surfaced 1Password etc."
                        var existingCo = _companies.GetByDomain(domain);
                        if (existingCo is not null)
                            _sightings.Record(existingCo.Id, "ai_competitor", $"peer of {seed.Name}", null, runId: null);
                        report.CompaniesSkippedExisting++;
                        continue;
                    }
                    if (!domainsThisRun.Add(domain))
                    {
                        report.CompaniesSkippedExisting++;
                        continue;
                    }

                    var newId = _companies.Insert(new Company
                    {
                        Id = 0,
                        Name = s.Name.Trim(),
                        Domain = domain,
                        WebsiteUrl = $"https://{domain}",
                        City = string.IsNullOrWhiteSpace(s.City) ? null : s.City.Trim(),
                        Notes = $"AI-suggested as a peer of {seed.Name}",
                        DateDiscovered = DateTime.UtcNow,
                    });
                    _sightings.Record(newId, "ai_competitor", $"peer of {seed.Name}", null, runId: null);
                    existing.Add(domain);
                    report.CompaniesAdded++;
                }
            }
            catch (Exception ex)
            {
                report.Errors.Add($"[{seed.Name}] {ex.GetType().Name}: {ex.Message}");
            }
        }
        return report;
    }

    private async Task<IReadOnlyList<Suggestion>> SuggestForAsync(Company seed, CancellationToken ct)
    {
        var profile = _companies.GetProfile(seed.Id);
        var hint = profile?.Summary ?? "";

        var system =
            "You suggest Canadian product technology companies that compete with or operate in the " +
            "same space as a target company. Output STRICT JSON only — no prose, no markdown. Schema:\n" +
            "{ \"competitors\": [\n" +
            "  { \"name\": \"Company name\", \"website\": \"company-domain.com\", \"city\": \"Vancouver\" }\n" +
            "] }\n" +
            "\n" +
            "Rules:\n" +
            "- Return 5 companies (or fewer if you genuinely can't think of more).\n" +
            "- Prefer Canadian companies, especially Vancouver/BC. Other Canadian metros (Toronto, Montreal) are OK as fallback.\n" +
            "- Prefer LESSER-KNOWN companies (Series A/B/C, not unicorns or FAANG).\n" +
            "- Output the company's primary website domain only (e.g. 'example.com'), no protocol/path.\n" +
            "- Skip the target company itself and the obvious giants (Stripe, Square, Shopify, Wealthsimple, Salesforce, etc. — unless they're the target's direct space and lesser-known peers don't exist).\n" +
            "- Never invent companies. If unsure, return fewer entries.";

        var user =
            $"Target company: {seed.Name} ({seed.Domain})\n" +
            (string.IsNullOrEmpty(hint) ? "" : $"Profile summary: {hint}\n") +
            "\nList 5 Canadian tech companies in similar or adjacent spaces (preferring lesser-known firms).";

        var resp = await _ai.CompleteAsync(user, system, maxTokens: 4096, ct, task: "competitors");
        return ParseSuggestions(resp.Text);
    }

    private static IReadOnlyList<Suggestion> ParseSuggestions(string responseText)
    {
        var list = new List<Suggestion>();
        var first = responseText.IndexOf('{');
        var last = responseText.LastIndexOf('}');
        if (first < 0 || last <= first) return list;
        var json = responseText.Substring(first, last - first + 1);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("competitors", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var elt in arr.EnumerateArray())
            {
                if (elt.ValueKind != JsonValueKind.Object) continue;
                list.Add(new Suggestion
                {
                    Name    = StrOrNull(elt, "name"),
                    Website = StrOrNull(elt, "website"),
                    City    = StrOrNull(elt, "city"),
                });
            }
        }
        catch { /* swallow */ }
        return list;
    }

    private static string CanonicalDomain(string website)
    {
        try
        {
            var u = website.Contains("://", StringComparison.Ordinal) ? website : $"https://{website}";
            var uri = new Uri(u);
            var host = uri.Host.ToLowerInvariant();
            if (host.StartsWith("www.", StringComparison.Ordinal)) host = host.Substring(4);
            return host;
        }
        catch { return ""; }
    }

    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft.com", "google.com", "amazon.com", "apple.com", "meta.com",
        "facebook.com", "oracle.com", "sap.com", "salesforce.com", "adobe.com",
        "ibm.com", "intel.com", "cisco.com", "stripe.com", "square.com", "block.xyz",
        "linkedin.com", "indeed.com", "glassdoor.com", "twitter.com", "x.com",
        "wellfound.com", "crunchbase.com",
    };
    private static bool IsBlockedDomain(string domain) => Blocked.Contains(domain);

    private static string? StrOrNull(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private sealed class Suggestion
    {
        public string? Name { get; set; }
        public string? Website { get; set; }
        public string? City { get; set; }
    }
}
