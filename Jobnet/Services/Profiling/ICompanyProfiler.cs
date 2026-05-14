using System.Threading;
using System.Threading.Tasks;
using Jobnet.Models;

namespace Jobnet.Services.Profiling;

public interface ICompanyProfiler
{
    /// <summary>Generate (and persist) a company profile by fetching its homepage and summarizing via Claude Haiku.</summary>
    Task<ProfileResult> GenerateAndPersistAsync(Company company, CancellationToken ct = default);
}

public sealed class ProfileResult
{
    public CompanyProfile? Profile { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? SourceUrl { get; init; }   // which URL the summary was based on
}
