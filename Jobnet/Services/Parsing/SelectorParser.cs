using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Jobnet.Services.AtsAdapters;

namespace Jobnet.Services.Parsing;

/// <summary>
/// Deterministic replayer that turns a cached <see cref="SelectorProfile"/> + rendered HTML
/// into a list of jobs. Replaces the per-refresh AI call for companies on the AI-extract
/// path once their profile has been derived. Pure CPU + DOM walk, no network, no LLM.
/// </summary>
public sealed class SelectorParser
{
    /// <summary>Parse the given HTML with the given profile JSON. Returns an empty list if
    /// the profile is invalid or no cards match (caller treats either case as a drift signal
    /// when the company was previously yielding jobs).</summary>
    public IReadOnlyList<RawJobPosting> Parse(string profileJson, string html, string baseUrl)
    {
        var profile = TryDeserializeProfile(profileJson);
        if (profile is null || !profile.IsValid())
            throw new SelectorParseException("Profile JSON is missing required fields or has an unsupported version.");

        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);

        IElement? scope = string.IsNullOrWhiteSpace(profile.Container)
            ? doc.DocumentElement
            : doc.QuerySelector(profile.Container);
        if (scope is null) return Array.Empty<RawJobPosting>();

        var cards = scope.QuerySelectorAll(profile.Card);
        if (cards.Length == 0) return Array.Empty<RawJobPosting>();

        // Dedupe by (title|url) — selector profiles sometimes match nested templates that
        // re-render the same posting twice (e.g. an "Apply now" overlay).
        var results = new List<RawJobPosting>(cards.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var card in cards)
        {
            var title = QueryValue(card, profile.Title);
            if (string.IsNullOrWhiteSpace(title)) continue;

            var rawUrl = QueryValue(card, profile.Url);
            var absoluteUrl = MakeAbsolute(rawUrl, baseUrl);

            var key = $"{title}|{absoluteUrl}";
            if (!seen.Add(key)) continue;

            results.Add(new RawJobPosting
            {
                NativeId = ShortHash(key),
                Title = title!.Trim(),
                Url = absoluteUrl,
                Location = QueryValue(card, profile.Location)?.Trim(),
                RemoteType = QueryValue(card, profile.RemoteType)?.Trim(),
                EmploymentType = QueryValue(card, profile.EmploymentType)?.Trim(),
                Department = QueryValue(card, profile.Department)?.Trim(),
            });
        }
        return results;
    }

    /// <summary>Evaluate a selector against the card. Returns trimmed text content, or the
    /// requested attribute when the selector ends with <c>@attrName</c>. Null when the
    /// selector is empty/null/no match.</summary>
    private static string? QueryValue(IElement card, string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector)) return null;

        string css = selector;
        string? attr = null;
        var at = selector.LastIndexOf('@');
        if (at > 0)
        {
            css = selector[..at];
            attr = selector[(at + 1)..];
        }

        IElement? target = card.QuerySelector(css);
        // The card itself is allowed to be the target — selector "@href" alone reads from the card.
        if (target is null && at == 0) target = card;
        if (target is null) return null;

        if (attr is not null)
            return target.GetAttribute(attr);

        // textContent collapses whitespace just like the existing AI prompt expects.
        return target.TextContent;
    }

    private static string? MakeAbsolute(string? href, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        if (Uri.TryCreate(href, UriKind.Absolute, out var abs)) return abs.ToString();
        if (Uri.TryCreate(new Uri(baseUrl), href, out var combined)) return combined.ToString();
        return href;
    }

    private static SelectorProfile? TryDeserializeProfile(string json)
    {
        try { return JsonSerializer.Deserialize<SelectorProfile>(json); }
        catch (JsonException) { return null; }
    }

    private static string ShortHash(string s)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}

public sealed class SelectorParseException : Exception
{
    public SelectorParseException(string message) : base(message) { }
}
