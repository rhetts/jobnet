using System.Threading;
using System.Threading.Tasks;
using Jobnet.Models;

namespace Jobnet.Services.CoverLetter;

public interface ICoverLetterGenerator
{
    /// <summary>Generate a cover letter from the stored resume + this job + the user's durable
    /// instructions (from config). Returns either the text or an error.</summary>
    Task<CoverLetterResult> GenerateAsync(Job job, string companyName, CancellationToken ct = default);
}

public sealed class CoverLetterResult
{
    public bool Success { get; init; }
    public string? Text { get; init; }
    public string? Model { get; init; }
    public string? Error { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
}
