using System.Threading;
using System.Threading.Tasks;

namespace Jobnet.Services.ApiUsage;

/// <summary>
/// Reacts to upstream 429 rate-limit responses. The AI clients call this when they see one;
/// the controller decides whether to silently throttle (per-minute) or pop a dialog (per-day).
/// </summary>
public interface IApiQuotaController
{
    /// <summary>The per-minute rate limit just kicked in. Increase the configured min-delay
    /// for this provider so subsequent calls have more breathing room. No UI.</summary>
    void OnPerMinuteLimit(string provider);

    /// <summary>The per-day quota looks exhausted. Show the user a dialog asking whether to
    /// retry anyway. Decision is cached for the rest of the session so we don't spam dialogs.</summary>
    Task<QuotaDecision> OnPerDayLimitAsync(string provider, string errorBody);

    /// <summary>Token that fires when the user clicks "No" on any per-day dialog. Batch loops
    /// in the ViewModel should pass this as their CancellationToken so the whole run unwinds
    /// instead of just the single AI call that triggered the dialog.</summary>
    CancellationToken SessionCancellationToken { get; }

    /// <summary>Reset session-wide cancellation + cached per-day decisions. Call at the start
    /// of every new batch command so a prior cancel doesn't immediately abort the new run.</summary>
    void ResetSession();

    /// <summary>Fire the session cancellation token now. Same effect as the user clicking "No"
    /// on a per-day quota popup, but driven from a UI Stop button instead of a 429.</summary>
    void CancelSession();

    /// <summary>True if the current session was cancelled because the user chose "No" on a
    /// per-day quota dialog (i.e. an actual cloud provider returned a daily-quota 429).
    /// False if the cancellation came from the user pressing Stop or any other CT trigger.
    /// Lets the UI distinguish "quota exhausted" from "user cancelled" / "internal timeout"
    /// — important when the active provider is local llama, which never produces a quota event.</summary>
    bool WasCancelledByDailyQuota { get; }
}

public enum QuotaDecision { Continue, Cancel }
