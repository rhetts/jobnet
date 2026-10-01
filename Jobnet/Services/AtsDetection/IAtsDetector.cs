using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Models;

namespace Jobnet.Services.AtsDetection;

public interface IAtsDetector
{
    /// <summary>Full detection, homepage-first: homepage → its careers links → one more hop, with
    /// at most two Playwright renders (JS-built nav, JS-loaded job widget). Use from the detect-ats
    /// CLI command or one-off scans.</summary>
    Task<AtsDetectionResult> DetectAsync(Company company, CancellationToken ct = default);

    /// <summary>Same homepage-first flow with no Playwright. Fast (a few fetches per company) but
    /// won't catch JS-rendered nav or ATS embeds. Use from the regular refresh loop to short-circuit
    /// AI extraction on companies whose careers page statically embeds a known ATS.</summary>
    Task<AtsDetectionResult> DetectViaHttpAsync(Company company, CancellationToken ct = default);

    /// <summary>Full detection with an explicit method — <see cref="AtsDetectionMethod.Legacy"/> is
    /// the old guess-every-URL flow, kept only so <c>detect-ats --eval</c> can compare the two.</summary>
    Task<AtsDetectionResult> DetectWithAsync(Company company, AtsDetectionMethod method, CancellationToken ct = default);
}

public sealed class AtsDetectionResult
{
    /// <summary>'greenhouse' | 'lever' | 'ashby' | 'workable' | 'smartrecruiters' | 'recruitee' | 'cornerstone' | ... | null</summary>
    public string? AtsType { get; init; }

    /// <summary>The company-specific identifier within the ATS (e.g. 'shopify' in boards.greenhouse.io/shopify).</summary>
    public string? AtsSlug { get; init; }

    /// <summary>The actual careers URL we landed on (after redirects). Persisted as careers_url.</summary>
    public string? ResolvedCareersUrl { get; init; }

    /// <summary>'redirect' | 'html_fingerprint' | 'none' — how we identified the ATS.</summary>
    public string Source { get; init; } = "none";

    public string? Notes { get; init; }

    /// <summary>What this result says about the company's health: <c>Clear</c> when an ATS was
    /// found (config is fine), a status when the site itself is the problem, and <c>Apply = false</c>
    /// for an ordinary miss (no evidence either way — leave whatever is recorded).</summary>
    public (bool Apply, string? Status, string? Reason) HealthUpdate()
    {
        if (AtsType is not null && AtsSlug is not null) return (true, null, null);
        var lastStep = Notes?.Split(" → ").LastOrDefault();
        return Source switch
        {
            "site_unreachable" => (true, Models.CompanyHealth.SiteDead, $"Homepage unreachable: {lastStep}"),
            "site_moved"       => (true, Models.CompanyHealth.SiteMoved, lastStep),
            "no_careers_page"  => (true, Models.CompanyHealth.NoCareersPage, "No careers link on the homepage and no careers page at the usual URLs"),
            _                  => (false, null, null),
        };
    }
}
