using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Jobnet.Data.Repositories;
using Jobnet.Services.Ai;
using Jobnet.Services.AtsAdapters;

namespace Jobnet.Services.Parsing;

/// <summary>
/// One-shot AI pass that turns a careers-page HTML into a reusable <see cref="SelectorProfile"/>.
/// Called the first time we encounter a non-native-ATS company (and on drift) — every subsequent
/// refresh replays the cached profile via <see cref="SelectorParser"/> with no AI involvement.
///
/// The deriver sanity-checks the AI's output by running the profile through SelectorParser
/// before returning. A profile that produces zero jobs against the very HTML it was derived
/// from is discarded — the caller treats that as a derivation failure and either falls back to
/// AI extraction for this refresh or surfaces it on the Parser Report screen.
/// </summary>
public sealed class AiSelectorDeriver
{
    private readonly IAiClient _ai;
    private readonly SelectorParser _parser;
    private readonly IConfigRepository _config;

    /// <summary>Conservative default sized to fit in a 4K-context local model alongside the
    /// system prompt (~500 tokens) and a 1K response budget. ~4 chars/token → 8000 chars ≈
    /// 2K tokens of input HTML. Larger online providers (Gemini, Claude) can lift this via
    /// the <c>selector_deriver_max_html_chars</c> config key.</summary>
    private const int DefaultMaxHtmlChars = 8_000;

    public AiSelectorDeriver(IAiClient ai, SelectorParser parser, IConfigRepository config)
    {
        _ai = ai;
        _parser = parser;
        _config = config;
    }

    private int MaxHtmlChars =>
        int.TryParse(_config.GetOrDefault("selector_deriver_max_html_chars", DefaultMaxHtmlChars.ToString()),
                      out var n) && n > 0 ? n : DefaultMaxHtmlChars;

    /// <summary>Forgiving deserializer options. The AI occasionally trails the object with a
    /// stray comma, includes a JS-style comment, or differs in casing — none of those should
    /// kill an otherwise-usable profile. Real syntactic errors (e.g. unescaped inner quotes)
    /// still fail and get logged via <see cref="Ai.AiLogger"/>.</summary>
    private static readonly JsonSerializerOptions LenientJsonOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<SelectorDeriveResult> DeriveAsync(string html, string baseUrl, CancellationToken ct = default)
    {
        if (!_ai.IsConfigured)
            return SelectorDeriveResult.Fail("AI client not configured.");

        var trimmedHtml = TrimHtmlForDerivation(html);
        if (string.IsNullOrWhiteSpace(trimmedHtml))
            return SelectorDeriveResult.Fail("Page produced no extractable HTML after trimming.");

        var system = BuildSystemPrompt();
        var maxChars = MaxHtmlChars;
        var user =
            $"Source URL: {baseUrl}\n\n" +
            $"Trimmed page HTML (noise stripped, capped at {maxChars} chars):\n" +
            "```html\n" + trimmedHtml + "\n```";

        // 2048 absorbs Tailwind-style class chains (>100 chars) and the model's occasional whitespace
        // padding without truncating mid-selector — we saw truncation at 1024 on LoginRadius and Rise People.
        var resp = await _ai.CompleteAsync(user, system, maxTokens: 2048, ct, task: "selector_derive");

        var profileJson = JsonExtractor.ExtractJsonObject(resp.Text);
        if (string.IsNullOrWhiteSpace(profileJson))
            return SelectorDeriveResult.Fail("AI returned empty response.");

        // Validate the JSON and required fields before persisting.
        SelectorProfile? profile;
        try { profile = JsonSerializer.Deserialize<SelectorProfile>(profileJson, LenientJsonOptions); }
        catch (JsonException ex)
        {
            // Persist the raw + extracted text so we can see exactly what the model emitted —
            // ex.Message alone (e.g. "Expected end of string...") is useless without context.
            Ai.AiLogger.LogParseFailure(
                taskTag: "selector_derive",
                exception: ex,
                rawResponse: resp.Text,
                extractedJson: profileJson,
                extraContext: $"baseUrl={baseUrl}");
            return SelectorDeriveResult.Fail($"AI response is not valid JSON: {ex.Message}");
        }
        if (profile is null || !profile.IsValid())
            return SelectorDeriveResult.Fail("AI response is missing required selector fields.");

        // Round-trip via the parser to confirm the selectors actually find jobs on the page
        // they were derived from. If the AI hallucinates selectors that match nothing, we'd
        // rather discover that now than silently persist a useless profile.
        IReadOnlyList<RawJobPosting> sanityJobs;
        try { sanityJobs = _parser.Parse(profileJson, html, baseUrl); }
        catch (Exception ex) { return SelectorDeriveResult.Fail($"Selector replay threw: {ex.Message}"); }

        if (sanityJobs.Count == 0)
            return SelectorDeriveResult.Fail(
                "Derived selectors produced 0 jobs against the source page.",
                proposedProfileJson: profileJson);

        // Normalize the persisted JSON so we don't store the AI's whitespace/key ordering.
        var normalized = JsonSerializer.Serialize(profile);
        return SelectorDeriveResult.Ok(normalized, sanityJobs);
    }

    private static string BuildSystemPrompt() =>
        "You derive a reusable CSS-selector profile for extracting job postings from a company's careers page.\n" +
        "Output STRICT JSON only — no prose, no markdown, no code fences. Match this schema exactly:\n" +
        "{\n" +
        "  \"version\": \"ai_selectors_v1\",\n" +
        "  \"container\": null,\n" +
        "  \"card\": \"<selector matching ONE element per job posting>\",\n" +
        "  \"title\": \"<selector relative to card, for the job title text>\",\n" +
        "  \"url\":   \"<selector relative to card, ending in @href for the link>\",\n" +
        "  \"location\": null,\n" +
        "  \"remote_type\": null,\n" +
        "  \"employment_type\": null,\n" +
        "  \"department\": null\n" +
        "}\n" +
        "\n" +
        "JSON syntax rules — read carefully, this is the #1 cause of broken responses:\n" +
        "- Every selector value is a JSON string, so it must be enclosed in DOUBLE quotes.\n" +
        "- Inside that string, any CSS attribute selector that needs quotes around a value MUST use\n" +
        "  SINGLE quotes — e.g. 'a[data-test=\\'apply-link\\']@href' or '[role=\\'button\\']'.\n" +
        "  Double quotes inside the string would terminate it early and break the JSON.\n" +
        "- No trailing comma after the last field. No comments. No code fences.\n" +
        "- Backslash-escape any literal '\\' or '\"' that genuinely needs to appear in a selector.\n" +
        "\n" +
        "Selector rules:\n" +
        "- Prefer stable, semantic class names over generated/utility ones (e.g. '.job-listing' over '.css-1abc23').\n" +
        "- The 'card' selector must match exactly one element per posting. Use a class that wraps the whole row.\n" +
        "- For URLs, ALWAYS use the '@attr' suffix to read an attribute: e.g. 'a.apply-link@href' or 'a@href'.\n" +
        "- For text fields, omit the '@attr' — the runtime reads textContent.\n" +
        "- A selector path uses standard CSS (descendant, child, attribute, pseudo-class). No XPath.\n" +
        "- Set optional fields to null when the page doesn't expose them as a structured element.\n" +
        "- Do NOT include placeholder selectors that 'might work' — null is better than a bad guess.\n" +
        "- The profile must work for FUTURE postings on this site, not just the ones visible right now.";

    /// <summary>Strip noise (script, style, svg, head, comments) and cap the body to the
    /// soft limit. AngleSharp normalizes the structure so the AI sees something close to
    /// what a human would see in DevTools, minus the chrome.</summary>
    private string TrimHtmlForDerivation(string html)
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);

        foreach (var selector in new[] { "script", "style", "svg", "noscript", "iframe", "link", "meta" })
        {
            foreach (var el in doc.QuerySelectorAll(selector))
                el.Remove();
        }

        var body = doc.Body;
        if (body is null) return string.Empty;

        var raw = body.InnerHtml ?? string.Empty;
        var max = MaxHtmlChars;
        if (raw.Length <= max) return raw;
        return raw[..max];
    }

}

public sealed class SelectorDeriveResult
{
    public bool Success { get; init; }
    public string? ProfileJson { get; init; }
    public IReadOnlyList<RawJobPosting> Jobs { get; init; } = Array.Empty<RawJobPosting>();
    public string? Error { get; init; }

    /// <summary>On failure paths where the AI did produce parseable JSON but it failed downstream
    /// validation (e.g. selectors matched 0 jobs), this carries the rejected profile so callers
    /// (CLI / debug tooling) can inspect what the model emitted.</summary>
    public string? ProposedProfileJson { get; init; }

    public static SelectorDeriveResult Ok(string profileJson, IReadOnlyList<RawJobPosting> jobs)
        => new() { Success = true, ProfileJson = profileJson, Jobs = jobs };

    public static SelectorDeriveResult Fail(string error, string? proposedProfileJson = null)
        => new() { Success = false, Error = error, ProposedProfileJson = proposedProfileJson };
}
