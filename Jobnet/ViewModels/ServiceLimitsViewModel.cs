using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jobnet.Services.ApiUsage;

namespace Jobnet.ViewModels;

public partial class ServiceLimitsViewModel : ObservableObject
{
    private readonly IApiUsageTracker _usage;

    public ObservableCollection<ProviderUsageTile> Providers { get; } = new();

    [ObservableProperty]
    private string _lastRefreshed = "(not refreshed yet)";

    public ServiceLimitsViewModel(IApiUsageTracker usage)
    {
        _usage = usage;
        Refresh();
    }

    [RelayCommand]
    public void Refresh()
    {
        Providers.Clear();
        var tiles = _usage.GetAllSnapshots()
            .Select(s => new ProviderUsageTile(s))
            .OrderByDescending(t => t.MaxFraction)
            .ThenBy(t => t.Provider, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var t in tiles) Providers.Add(t);
        LastRefreshed = $"Refreshed {DateTime.Now:HH:mm:ss}";
    }
}

public sealed class ProviderUsageTile
{
    public string Provider { get; }
    public string Rpd { get; }
    public string Rpm { get; }
    public string Tpd { get; }
    public string LastCall { get; }

    public double RpdFraction { get; }
    public double RpmFraction { get; }
    public double TpdFraction { get; }

    public string RpdStatusBrush { get; }
    public string RpmStatusBrush { get; }
    public string TpdStatusBrush { get; }

    public bool HasTpdLimit { get; }

    /// <summary>Highest fill ratio across the dimensions we display — used to sort tiles
    /// "closest to cap first". TPM is intentionally excluded: its 60-second rolling window
    /// is too transient to be useful on a manually-refreshed dashboard.</summary>
    public double MaxFraction { get; }

    public ProviderUsageTile(ApiUsageSnapshot s)
    {
        Provider = s.Provider;

        Rpd = s.RpdCap > 0 ? $"{s.Rpd} / {s.RpdCap}" : $"{s.Rpd}";
        Rpm = s.RpmCap > 0 ? $"{s.Rpm} / {s.RpmCap}" : $"{s.Rpm}";
        Tpd = s.TokensTodayCap > 0 ? $"{s.TokensToday:N0} / {s.TokensTodayCap:N0}" : (s.TokensToday > 0 ? $"{s.TokensToday:N0}" : "—");
        LastCall = s.LastCallUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "(never today)";

        RpdFraction = s.RpdCap > 0 ? Math.Min(1.0, (double)s.Rpd / s.RpdCap) : 0;
        RpmFraction = s.RpmCap > 0 ? Math.Min(1.0, (double)s.Rpm / s.RpmCap) : 0;
        TpdFraction = s.TokensTodayCap > 0 ? Math.Min(1.0, (double)s.TokensToday / s.TokensTodayCap) : 0;

        RpdStatusBrush = BrushFor(RpdFraction);
        RpmStatusBrush = BrushFor(RpmFraction);
        TpdStatusBrush = BrushFor(TpdFraction);

        HasTpdLimit = s.TokensTodayCap > 0;

        MaxFraction = Math.Max(TpdFraction, Math.Max(RpmFraction, RpdFraction));
    }

    private static string BrushFor(double frac) =>
        frac >= 0.90 ? "#C44" :  // red
        frac >= 0.70 ? "#E08E0B" : // amber
        "#2A8F4F";                 // green
}
