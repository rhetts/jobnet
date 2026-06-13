using System.Text.Json.Serialization;

namespace Jobnet.Services.Parsing;

/// <summary>
/// AI-derived "how to find jobs on this site" profile. Stored as JSON in
/// companies.parser_strategy and replayed deterministically by <see cref="SelectorProfileReplayer"/>
/// in place of the per-refresh AI extraction call.
///
/// Selector syntax: CSS selector for the element, with an optional <c>@attr</c> suffix
/// to pull an attribute instead of text content (e.g. <c>a.apply-link@href</c>).
/// </summary>
public sealed class SelectorProfile
{
    /// <summary>Schema version. Bumped when the JSON shape changes so the replayer can
    /// reject incompatible profiles and trigger AI re-derivation.</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "ai_selectors_v1";

    /// <summary>Optional parent selector to scope the search. When set, <see cref="Card"/>
    /// is queried inside this container. When null, the document root is used.</summary>
    [JsonPropertyName("container")]
    public string? Container { get; set; }

    /// <summary>Required. CSS selector matching one element per job posting.</summary>
    [JsonPropertyName("card")]
    public string Card { get; set; } = "";

    /// <summary>Required. Selector (relative to a card) for the job title text.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>Required. Selector for the job URL. Usually <c>a@href</c> or similar.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("remote_type")]
    public string? RemoteType { get; set; }

    [JsonPropertyName("employment_type")]
    public string? EmploymentType { get; set; }

    [JsonPropertyName("department")]
    public string? Department { get; set; }

    public bool IsValid() =>
        Version == "ai_selectors_v1"
        && !string.IsNullOrWhiteSpace(Card)
        && !string.IsNullOrWhiteSpace(Title)
        && !string.IsNullOrWhiteSpace(Url);
}
