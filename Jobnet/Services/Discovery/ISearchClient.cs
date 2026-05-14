using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jobnet.Services.Discovery;

public interface ISearchClient
{
    /// <summary>Friendly name of the provider, e.g. "google_cse" or "brave_search".</summary>
    string ProviderId { get; }

    /// <summary>Run a search query.  Throws if credentials are not configured.</summary>
    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int page = 1, int pageSize = 10, CancellationToken ct = default);
}

public sealed class SearchResult
{
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string? Snippet { get; init; }
    public string? DisplayLink { get; init; }
}

public sealed class SearchAuthException : System.Exception
{
    public SearchAuthException(string message) : base(message) { }
}
