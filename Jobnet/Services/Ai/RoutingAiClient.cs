using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jobnet.Data.Repositories;
using Jobnet.Services.ApiUsage;

namespace Jobnet.Services.Ai;

/// <summary>
/// Selects the active AI client(s) from <c>ai_provider</c> config and applies fallback.
/// Accepted values:
///   <c>gemini</c>  → Gemini only
///   <c>groq</c>    → Groq only
///   <c>claude</c>  → Claude only (paid)
///   <c>both</c> / <c>gemini+groq</c> / <c>auto</c> → Gemini, fall back to Groq on failure
///   <c>groq+gemini</c> → reversed primary
/// </summary>
public sealed class RoutingAiClient : IAiClient
{
    private readonly GeminiClient _gemini;
    private readonly ClaudeClient _claude;
    private readonly GroqClient   _groq;
    private readonly LLamaClient  _llama;
    private readonly IConfigRepository _config;
    private readonly IApiQuotaController _quota;

    public RoutingAiClient(GeminiClient gemini, ClaudeClient claude, GroqClient groq, LLamaClient llama,
                            IConfigRepository config, IApiQuotaController quota)
    {
        _gemini = gemini;
        _claude = claude;
        _groq   = groq;
        _llama  = llama;
        _config = config;
        _quota  = quota;
    }

    public string ProviderId => "routing";
    public bool IsConfigured
    {
        get
        {
            foreach (var c in ResolveChain(null)) if (c.IsConfigured) return true;
            return false;
        }
    }

    public async Task<AiResponse> CompleteAsync(string userMessage, string? system = null, int? maxTokens = null, CancellationToken ct = default, string? task = null)
    {
        var chain = ResolveChain(task);
        Exception? last = null;
        for (var i = 0; i < chain.Count; i++)
        {
            var c = chain[i];
            if (!c.IsConfigured) continue;
            try
            {
                return await c.CompleteAsync(userMessage, system, maxTokens, ct, task);
            }
            catch (AiUnavailableException ex)
            {
                last = ex;
                var message = ex.Message ?? "";
                var isDaily = message.Contains("daily", StringComparison.OrdinalIgnoreCase)
                           || message.Contains("RPD", StringComparison.OrdinalIgnoreCase)
                           || message.Contains("per_day", StringComparison.OrdinalIgnoreCase);

                var hasMoreFallbacks = AnyConfiguredAfter(chain, i);
                if (hasMoreFallbacks)
                {
                    // Silently fall over to the next provider. If this was a daily-quota error,
                    // remember it for the rest of the session so future calls skip this provider
                    // entirely — no point repeatedly burning a round-trip on a known-exhausted
                    // provider only to fail over again.
                    if (isDaily) _quota.MarkProviderExhausted(c.ProviderId);
                    continue;
                }
                // Last-resort: classify as RPM or RPD and surface the controller dialog only here.
                if (isDaily)
                {
                    _quota.MarkProviderExhausted(c.ProviderId);
                    var decision = await _quota.OnPerDayLimitAsync(c.ProviderId, message);
                    if (decision == QuotaDecision.Cancel)
                        throw new OperationCanceledException($"{c.ProviderId} daily quota — user cancelled.");
                    // user said Continue but we have no more providers; just re-throw
                }
                else
                {
                    _quota.OnPerMinuteLimit(c.ProviderId);
                }
                throw;
            }
        }
        throw last ?? new AiUnavailableException("No AI provider configured. Set Gemini and/or Groq API key in Settings.");
    }

    private static bool AnyConfiguredAfter(IReadOnlyList<IAiClient> chain, int idx)
    {
        for (var j = idx + 1; j < chain.Count; j++) if (chain[j].IsConfigured) return true;
        return false;
    }

    private IReadOnlyList<IAiClient> ResolveChain(string? task)
    {
        // Prefer per-task chain (ai_provider.{task}); fall back to the global ai_provider.
        var raw = "";
        if (!string.IsNullOrWhiteSpace(task))
            raw = _config.GetOrDefault($"ai_provider.{task}", "").Trim();
        if (string.IsNullOrEmpty(raw))
            raw = _config.GetOrDefault("ai_provider", "gemini").Trim();

        raw = raw.ToLowerInvariant();
        // Legacy aliases that mean "Gemini first, then Groq".
        if (raw is "both" or "auto") raw = "gemini,groq";

        var chain = new List<IAiClient>();
        var seen  = new HashSet<string>();
        void Add(IAiClient c, string id) { if (seen.Add(id)) chain.Add(c); }

        foreach (var tok in raw.Replace("+", ",").Split(',', System.StringSplitOptions.RemoveEmptyEntries))
        {
            var name = tok.Trim();
            switch (name)
            {
                case "gemini": case "google":    Add(_gemini, "gemini"); break;
                case "groq":                     Add(_groq,   "groq");   break;
                case "claude": case "anthropic": Add(_claude, "claude"); break;
                case "llama":  case "local":     Add(_llama,  "llama");  break;
            }
        }
        if (chain.Count == 0) Add(_gemini, "gemini");

        // Auto-pair Gemini and Groq. The two free-tier cloud providers cover for each other:
        // when the user picks just one of them, silently append the other as a fallback so a
        // single-provider RPD-out doesn't strand the caller. Honors the user's primary choice
        // — we only append, never reorder.
        if (seen.Contains("gemini") && !seen.Contains("groq") && _groq.IsConfigured)
            Add(_groq, "groq");
        else if (seen.Contains("groq") && !seen.Contains("gemini") && _gemini.IsConfigured)
            Add(_gemini, "gemini");

        // Drop providers known to be RPD-exhausted in this session. Keeps the chain from burning
        // a wasted round-trip on a provider we already failed over from. Reset on ResetSession().
        // Safety net: if every provider gets filtered out, fall back to the original chain so
        // the caller still gets a meaningful "daily quota" error instead of a misleading
        // "no provider configured" one.
        var pruned = chain.Where(c => !_quota.IsProviderExhausted(c.ProviderId)).ToList();
        return pruned.Count > 0 ? pruned : chain;
    }

    /// <summary>The set of provider IDs this router knows how to instantiate, in display order.
    /// Used by the AI Routing settings UI to populate the chain editor.</summary>
    public static readonly System.Collections.Generic.IReadOnlyList<string> KnownProviders =
        new[] { "gemini", "groq", "claude", "llama" };

    /// <summary>The set of task tags the rest of the codebase passes. Display order matches the
    /// settings UI. Keep this in sync with the strings used at each call site.</summary>
    public static readonly System.Collections.Generic.IReadOnlyList<string> KnownTasks =
        new[] { "extraction", "selector_derive", "directory", "resume_match", "summary", "profile", "cover_letter", "classifier", "competitors" };

    /// <summary>Human-readable label for each task tag, parallel-indexed to <see cref="KnownTasks"/>.
    /// Surfaced on the AI-routing settings tab so users see "Job extraction" instead of "extraction".</summary>
    public static readonly System.Collections.Generic.IReadOnlyList<string> KnownTaskLabels =
        new[] {
            "Job extraction (careers-page AI parse)",
            "Selector derivation (one-shot CSS profile)",
            "Company directory harvest",
            "Resume-to-job match scoring",
            "Job description summary",
            "Company profile generation",
            "Cover letter draft",
            "Job classifier fallback",
            "Competitor company suggestions",
        };
}
