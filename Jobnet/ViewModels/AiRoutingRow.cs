using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Jobnet.ViewModels;

/// <summary>One row on the Settings → AI routing tab. Each row maps to a config key
/// <c>ai_provider.{TaskKey}</c> which is a comma-separated provider chain. A row with
/// <c>Provider = "Default"</c> means "delete the override and use the global ai_provider".</summary>
public partial class AiRoutingRow : ObservableObject
{
    /// <summary>Stable task tag passed at the call site (e.g. "extraction"). Maps to the
    /// config key <c>ai_provider.{TaskKey}</c>. Not user-visible.</summary>
    public string TaskKey { get; }

    /// <summary>Human-readable label rendered in the table.</summary>
    public string Label { get; }

    /// <summary>Primary provider for this task. "Default" means inherit the global chain.</summary>
    [ObservableProperty]
    private string _provider = "Default";

    /// <summary>When true and provider is online, append "llama" as a fallback in the chain.
    /// Bound to the "Fall back to local" checkbox.</summary>
    [ObservableProperty]
    private bool _fallbackToLocal;

    /// <summary>True when the fallback checkbox should be enabled — only meaningful when the
    /// primary provider is an online one (not "Default", not "llama").</summary>
    public bool CanFallBack => Provider != "Default" && Provider != "llama";

    partial void OnProviderChanged(string value)
    {
        OnPropertyChanged(nameof(CanFallBack));
        // When the user switches to "Default" or "llama", the fallback checkbox becomes
        // meaningless — clear it so Save() doesn't persist a useless "llama,llama" chain.
        if (!CanFallBack) FallbackToLocal = false;
    }

    public AiRoutingRow(string taskKey, string label)
    {
        TaskKey = taskKey;
        Label = label;
    }

    public static IReadOnlyList<string> ProviderOptions { get; } =
        new[] { "Default", "gemini", "claude", "groq", "llama" };

    /// <summary>Build the chain string to store in config from the current row state.
    /// Empty string means "delete the key" (let the global default win).</summary>
    public string ToChainString()
    {
        if (Provider == "Default") return "";
        if (Provider == "llama") return "llama";
        return FallbackToLocal ? $"{Provider},llama" : Provider;
    }

    /// <summary>Apply a stored chain string from config, the inverse of <see cref="ToChainString"/>.</summary>
    public void ApplyChainString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            Provider = "Default";
            FallbackToLocal = false;
            return;
        }
        var parts = raw.Replace("+", ",")
                       .Split(',', System.StringSplitOptions.RemoveEmptyEntries);
        var primary = parts.Length > 0 ? parts[0].Trim().ToLowerInvariant() : "";
        primary = primary switch
        {
            "google" => "gemini",
            "anthropic" => "claude",
            "local" => "llama",
            _ => primary
        };
        Provider = primary is "gemini" or "claude" or "groq" or "llama" ? primary : "Default";

        FallbackToLocal = CanFallBack
            && parts.Length > 1
            && System.Array.Exists(parts, p =>
            {
                var n = p.Trim().ToLowerInvariant();
                return n is "llama" or "local";
            });
    }
}
