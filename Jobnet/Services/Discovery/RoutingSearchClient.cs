using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Data.Repositories;

namespace Jobnet.Services.Discovery;

/// <summary>Selects the concrete search client based on the `search_engine` config value at call time.</summary>
public sealed class RoutingSearchClient : ISearchClient
{
    private readonly GoogleCseClient _google;
    private readonly BraveSearchClient _brave;
    private readonly IConfigRepository _config;

    public RoutingSearchClient(GoogleCseClient google, BraveSearchClient brave, IConfigRepository config)
    {
        _google = google;
        _brave = brave;
        _config = config;
    }

    public string ProviderId => Resolve().ProviderId;

    public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int page = 1, int pageSize = 10, CancellationToken ct = default) =>
        Resolve().SearchAsync(query, page, pageSize, ct);

    private ISearchClient Resolve()
    {
        var which = _config.GetOrDefault("search_engine", "brave_search").ToLowerInvariant();
        return which switch
        {
            "brave_search" or "brave"        => _brave,
            "google_cse" or "google" or "cse" => _google,
            _ => throw new InvalidOperationException(
                $"Unknown search_engine '{which}'. Set to 'brave_search' or 'google_cse' in Settings.")
        };
    }
}
