using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jobnet.Data.Repositories;
using Jobnet.Models;

namespace Jobnet.Services.Filters;

/// <summary>
/// An immutable, compiled snapshot of the filter_rule table. Load once per batch and reuse —
/// IConfigRepository-style per-call loading would mean a DB round trip per URL.
///
/// Evaluation order is allow-then-block: an <see cref="FilterAction.Allow"/> rule that matches
/// wins outright, so a later phase can express LocationMatcher's "any Canada signal beats a US
/// signal" without special-casing it here.
///
/// Match counts accumulate in memory and are written back by <see cref="FlushHits"/> at the end
/// of a run. Losing them on a crash is harmless — they're diagnostics, not state.
/// </summary>
public sealed class FilterRuleSet
{
    private readonly IFilterRuleRepository? _repo;
    private readonly IReadOnlyList<CompiledRule> _rules;
    private readonly ConcurrentDictionary<int, int> _hits = new();

    /// <summary>Patterns that failed to compile, as "pattern — reason". Surfaced in the Settings
    /// UI. GreylistMatcher can afford to swallow these because its tokens are Regex.Escape'd;
    /// these are user-authored and a silent drop would mean a rule that looks active but isn't.</summary>
    public IReadOnlyList<string> CompileErrors { get; }

    public int Count => _rules.Count;

    private FilterRuleSet(IFilterRuleRepository? repo, IReadOnlyList<CompiledRule> rules,
                           IReadOnlyList<string> errors)
    {
        _repo = repo;
        _rules = rules;
        CompileErrors = errors;
    }

    public static FilterRuleSet Load(IFilterRuleRepository repo)
        => Build(repo, repo.GetEnabled());

    /// <summary>Compile an explicit rule list. Used by tests and by the Settings UI to validate
    /// edits before saving.</summary>
    public static FilterRuleSet FromRules(IEnumerable<FilterRule> rules)
        => Build(null, rules.ToList());

    public static FilterRuleSet Empty() => new(null, Array.Empty<CompiledRule>(), Array.Empty<string>());

    private static FilterRuleSet Build(IFilterRuleRepository? repo, IReadOnlyList<FilterRule> rules)
    {
        var compiled = new List<CompiledRule>(rules.Count);
        var errors = new List<string>();

        foreach (var r in rules)
        {
            if (!r.IsEnabled) continue;
            if (string.IsNullOrWhiteSpace(r.Pattern)) continue;
            try
            {
                compiled.Add(CompiledRule.From(r));
            }
            catch (Exception ex)
            {
                errors.Add($"{r.Pattern} — {ex.Message}");
            }
        }
        return new FilterRuleSet(repo, compiled, errors);
    }

    /// <summary>True when this URL should not be fetched or persisted. Tests url-subject rules
    /// against the whole URL AND host-subject rules against its host — so a single
    /// "linkedin.com" host rule also stops https://linkedin.com/jobs/view/123 from being
    /// classified as a job board, which the four legacy lists never managed.</summary>
    public bool IsUrlBlocked(string? url, string? scope = FilterScope.Crawl)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        string? host = null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) host = uri.Host;

        // Allow wins over block, whichever subject it came from.
        if (MatchFirst(FilterSubject.Url, FilterAction.Allow, url, scope) is not null) return false;
        if (host is not null && MatchFirst(FilterSubject.Host, FilterAction.Allow, host, scope) is not null)
            return false;

        var hit = MatchFirst(FilterSubject.Url, FilterAction.Block, url, scope)
               ?? (host is not null ? MatchFirst(FilterSubject.Host, FilterAction.Block, host, scope) : null);
        if (hit is null) return false;

        _hits.AddOrUpdate(hit.Id, 1, (_, n) => n + 1);
        return true;
    }

    /// <summary>True when this host should not become a company we track, or be followed.</summary>
    public bool IsHostBlocked(string? host, string? scope = FilterScope.Discovery)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (MatchFirst(FilterSubject.Host, FilterAction.Allow, host, scope) is not null) return false;

        var hit = MatchFirst(FilterSubject.Host, FilterAction.Block, host, scope);
        if (hit is null) return false;

        _hits.AddOrUpdate(hit.Id, 1, (_, n) => n + 1);
        return true;
    }

    /// <summary>Generic form for the subjects added in later phases (job_title, location, …).</summary>
    public bool Matches(string subject, string action, string? value, string? scope = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var hit = MatchFirst(subject, action, value, scope);
        if (hit is null) return false;
        _hits.AddOrUpdate(hit.Id, 1, (_, n) => n + 1);
        return true;
    }

    /// <summary>The rule that blocked this URL, for diagnostics. Does not count as a hit.</summary>
    public FilterRule? ExplainUrl(string? url, string? scope = FilterScope.Crawl)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        string? host = null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) host = uri.Host;

        var hit = MatchFirst(FilterSubject.Url, FilterAction.Block, url, scope)
               ?? (host is not null ? MatchFirst(FilterSubject.Host, FilterAction.Block, host, scope) : null);
        return hit?.Source;
    }

    private CompiledRule? MatchFirst(string subject, string action, string value, string? scope)
    {
        foreach (var r in _rules)
        {
            if (!string.Equals(r.Subject, subject, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(r.Action, action, StringComparison.OrdinalIgnoreCase)) continue;
            // A null rule scope means "everywhere". A non-null one must match the caller's scope.
            if (r.Scope is not null && !string.Equals(r.Scope, scope, StringComparison.OrdinalIgnoreCase))
                continue;
            if (r.IsMatch(value)) return r;
        }
        return null;
    }

    /// <summary>Write accumulated hit counts back and reset them. Safe to call with nothing
    /// pending, and safe to never call at all.</summary>
    public void FlushHits()
    {
        if (_repo is null || _hits.IsEmpty) return;
        var snapshot = _hits.ToArray().ToDictionary(kv => kv.Key, kv => kv.Value);
        _hits.Clear();
        try
        {
            _repo.RecordHits(snapshot);
        }
        catch
        {
            // Diagnostics must never break a run.
        }
    }

    private sealed class CompiledRule
    {
        public required int Id { get; init; }
        public required string Subject { get; init; }
        public required string Action { get; init; }
        public required string? Scope { get; init; }
        public required FilterRule Source { get; init; }

        private Regex? _regex;
        private string _literal = "";
        private string _matchType = "";

        public static CompiledRule From(FilterRule r)
        {
            var c = new CompiledRule
            {
                Id = r.Id,
                Subject = r.Subject,
                Action = r.Action,
                Scope = r.Scope,
                Source = r,
                _matchType = r.MatchType,
            };

            switch (r.MatchType)
            {
                case FilterMatchType.Regex:
                    // Throws on a malformed pattern — Build() turns that into a CompileError.
                    c._regex = new Regex(r.Pattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
                    break;

                case FilterMatchType.Word:
                    c._regex = new Regex($@"\b{Regex.Escape(r.Pattern)}\b",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
                    break;

                case FilterMatchType.Domain:
                    c._literal = Normalize(r.Pattern);
                    break;

                case FilterMatchType.Substring:
                case FilterMatchType.Exact:
                    c._literal = r.Pattern.ToLowerInvariant();
                    break;

                default:
                    throw new ArgumentException($"unknown match_type '{r.MatchType}'");
            }
            return c;
        }

        public bool IsMatch(string value) => _matchType switch
        {
            FilterMatchType.Regex     => _regex!.IsMatch(value),
            FilterMatchType.Word      => _regex!.IsMatch(value),
            FilterMatchType.Substring => value.Contains(_literal, StringComparison.OrdinalIgnoreCase),
            FilterMatchType.Exact     => string.Equals(value, _literal, StringComparison.OrdinalIgnoreCase),
            FilterMatchType.Domain    => IsDomainMatch(Normalize(value)),
            _                         => false,
        };

        /// <summary>Exact host, or any subdomain of it. "linkedin.com" matches "ca.linkedin.com"
        /// but not "mylinkedin.com" — the suffix check requires a dot boundary. This is the
        /// semantics DomainExtractor had and the other three legacy lists lacked.</summary>
        private bool IsDomainMatch(string host)
            => host.Equals(_literal, StringComparison.Ordinal)
            || host.EndsWith("." + _literal, StringComparison.Ordinal);

        private static string Normalize(string host)
        {
            var h = host.Trim().ToLowerInvariant();
            if (h.StartsWith("www.", StringComparison.Ordinal)) h = h[4..];
            return h;
        }
    }
}
