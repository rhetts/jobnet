using System.Threading;
using System.Threading.Tasks;
using Jobnet.Models;

namespace Jobnet.Services.AtsDetection;

public interface IAtsDetector
{
    /// <summary>Full detection: HTTP probing, slug guess, and Playwright fallback. Slow on misses
    /// (~30s if Playwright is reached). Use from the detect-ats CLI command or one-off scans.</summary>
    Task<AtsDetectionResult> DetectAsync(Company company, CancellationToken ct = default);

    /// <summary>HTTP-only detection (no Playwright). Fast (~1-5s per company) but won't catch
    /// JS-rendered ATS embeds. Use from the regular refresh loop to short-circuit AI extraction
    /// on companies whose careers page statically embeds a known ATS.</summary>
    Task<AtsDetectionResult> DetectViaHttpAsync(Company company, CancellationToken ct = default);
}

public sealed class AtsDetectionResult
{
    /// <summary>'greenhouse' | 'lever' | 'ashby' | 'workable' | 'smartrecruiters' | 'recruitee' | null</summary>
    public string? AtsType { get; init; }

    /// <summary>The company-specific identifier within the ATS (e.g. 'shopify' in boards.greenhouse.io/shopify).</summary>
    public string? AtsSlug { get; init; }

    /// <summary>The actual careers URL we landed on (after redirects). Persisted as careers_url.</summary>
    public string? ResolvedCareersUrl { get; init; }

    /// <summary>'redirect' | 'html_fingerprint' | 'none' — how we identified the ATS.</summary>
    public string Source { get; init; } = "none";

    public string? Notes { get; init; }
}
