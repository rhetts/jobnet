using System.Collections.Generic;

namespace Jobnet.Services.Discovery.Strategies;

/// <summary>Builds the live list of discovery strategies on each call. Used by the
/// Refresh popup so that edits in the Settings "Discovery" tab take effect immediately
/// (without app restart).</summary>
public interface IDiscoveryStrategyProvider
{
    IReadOnlyList<IDiscoveryStrategy> GetAll();

    /// <summary>Brave/Google CSE keyword discovery.</summary>
    IDiscoveryStrategy GetWebSearch();

    /// <summary>One strategy per enabled row in discovery_seeds (Settings → Directories tab).</summary>
    IReadOnlyList<IDiscoveryStrategy> GetDirectoryStrategies();

    /// <summary>One strategy per enabled row in aggregator_sources (Settings → Boards tab).</summary>
    IReadOnlyList<IDiscoveryStrategy> GetBoardStrategies();
}
