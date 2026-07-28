using System.Threading;
using System.Threading.Tasks;
using Jobnet.Models;

namespace Jobnet.Services.CoverLetter;

public interface ICoverLetterGenerator
{
    /// <summary>Generate a cover letter from the stored resume + this job + the user's durable
    /// instructions (from config). Returns either the text or an error.</summary>
    Task<CoverLetterResult> GenerateAsync(Job job, string companyName, CancellationToken ct = default);

    /// <summary>Generate a cover letter from a raw job-posting URL. The page is fetched (with JS
    /// rendering via Playwright), text-extracted, and handed to the AI as a single prompt that
    /// returns JSON containing the role title, company name, and the letter. The detected title
    /// and company are surfaced on <see cref="CoverLetterResult"/> so the UI can label the
    /// letter without parsing the page itself.</summary>
    Task<CoverLetterResult> GenerateFromUrlAsync(string url, CancellationToken ct = default);
}

public sealed class CoverLetterResult
{
    public bool Success { get; init; }
    public string? Text { get; init; }
    public string? Model { get; init; }
    public string? Error { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }

    /// <summary>Role title parsed from the posting when generated via <c>GenerateFromUrlAsync</c>.
    /// Null for the Job-based flow (the caller already has the title).</summary>
    public string? DetectedTitle { get; init; }

    /// <summary>Company name parsed from the posting when generated via <c>GenerateFromUrlAsync</c>.
    /// Null for the Job-based flow.</summary>
    public string? DetectedCompany { get; init; }
}
