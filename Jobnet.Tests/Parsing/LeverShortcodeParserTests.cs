using System.Linq;
using Jobnet.Services.Parsing.HtmlPatternParsers;
using Jobnet.Tests.Fixtures;

namespace Jobnet.Tests.Parsing;

/// <summary>
/// Tests for <see cref="LeverShortcodeParser"/> driven by captured HTML fixtures from real
/// careers pages. Adding another company that uses the same shortcode = drop its HTML into
/// Fixtures/ and add a fact here.
/// </summary>
public class LeverShortcodeParserTests
{
    private readonly LeverShortcodeParser _parser = new();

    // ── CanHandle ───────────────────────────────────────────────────────────

    [Fact]
    public void CanHandle_returns_true_for_blackbird_fixture()
    {
        var html = FixtureLoader.Load("blackbird-interactive.html");
        Assert.True(_parser.CanHandle("https://blackbirdinteractive.com/careers-2/", html));
    }

    [Fact]
    public void CanHandle_returns_false_when_html_is_empty()
    {
        Assert.False(_parser.CanHandle("https://example.com/", ""));
    }

    [Fact]
    public void CanHandle_returns_false_when_only_one_marker_present()
    {
        // The wrapping class alone (without lever-job items) is not enough — it could be
        // a coincidental match on a page that uses the word "lever" elsewhere.
        var html = "<html><body><ul class=\"lever\"></ul></body></html>";
        Assert.False(_parser.CanHandle("https://example.com/", html));
    }

    [Fact]
    public void CanHandle_returns_true_on_minimal_valid_markup()
    {
        var html = "<ul class=\"lever\"><li class=\"lever-job\" id=\"x\"></li></ul>";
        Assert.True(_parser.CanHandle("https://example.com/", html));
    }

    // ── Parse: real fixture ─────────────────────────────────────────────────

    [Fact]
    public void Parse_blackbird_finds_the_expected_jobs()
    {
        var html = FixtureLoader.Load("blackbird-interactive.html");
        var jobs = _parser.Parse(html, "https://blackbirdinteractive.com/careers-2/");

        Assert.NotEmpty(jobs);
        // Spot-check one job we manually verified is in the fixture.
        var animator = jobs.SingleOrDefault(j => j.NativeId == "846b311f-81f8-48cd-b96b-0f742f668a5f");
        Assert.NotNull(animator);
        Assert.Equal("Animator (3-month contract)", animator!.Title);
        Assert.Equal(
            "https://jobs.lever.co/blackbirdinteractive/846b311f-81f8-48cd-b96b-0f742f668a5f",
            animator.Url);
    }

    [Fact]
    public void Parse_blackbird_returns_at_least_a_dozen_postings()
    {
        // The page had 14 Apply anchors at capture time. We tolerate the exact count drifting
        // by checking a floor — Blackbird isn't shrinking to fewer than 12 openings overnight.
        var html = FixtureLoader.Load("blackbird-interactive.html");
        var jobs = _parser.Parse(html, "https://blackbirdinteractive.com/careers-2/");
        Assert.True(jobs.Count >= 12, $"Expected ≥12 jobs, got {jobs.Count}");
    }

    [Fact]
    public void Parse_blackbird_every_job_has_required_fields()
    {
        var html = FixtureLoader.Load("blackbird-interactive.html");
        var jobs = _parser.Parse(html, "https://blackbirdinteractive.com/careers-2/");
        foreach (var j in jobs)
        {
            Assert.False(string.IsNullOrWhiteSpace(j.NativeId), "every job must have a NativeId");
            Assert.False(string.IsNullOrWhiteSpace(j.Title),    "every job must have a Title");
            Assert.False(string.IsNullOrWhiteSpace(j.Url),      "every job must have a Url");
            Assert.StartsWith("https://jobs.lever.co/", j.Url);
        }
    }

    [Fact]
    public void Parse_blackbird_native_ids_are_unique()
    {
        // Lever's posting IDs are GUIDs and the source-of-truth for dedup. Catch any future
        // case where the parser accidentally double-counts a posting (e.g. nested templates).
        var html = FixtureLoader.Load("blackbird-interactive.html");
        var jobs = _parser.Parse(html, "https://blackbirdinteractive.com/careers-2/");
        var ids = jobs.Select(j => j.NativeId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    // ── Parse: edge cases on synthetic markup ───────────────────────────────

    [Fact]
    public void Parse_returns_empty_when_no_jobs()
    {
        var html = "<html><body><ul class=\"lever\"></ul></body></html>";
        var jobs = _parser.Parse(html, "https://example.com/");
        Assert.Empty(jobs);
    }

    [Fact]
    public void Parse_skips_items_missing_required_fields()
    {
        // Three <li>: one missing id, one missing title, one valid. Only the valid one survives.
        var html = """
            <ul class="lever">
              <li class="lever-job"><h2 class="lever-job-title">No ID</h2><a class="lever-job-apply" href="/x">A</a></li>
              <li class="lever-job" id="no-title"><a class="lever-job-apply" href="/x">A</a></li>
              <li class="lever-job" id="ok-id"><h2 class="lever-job-title">Ok Title</h2><a class="lever-job-apply" href="/y">A</a></li>
            </ul>
            """;
        var jobs = _parser.Parse(html, "https://example.com/");
        Assert.Single(jobs);
        Assert.Equal("ok-id", jobs[0].NativeId);
        Assert.Equal("Ok Title", jobs[0].Title);
    }

    [Fact]
    public void Parse_resolves_relative_hrefs_against_base_url()
    {
        var html = """
            <ul class="lever">
              <li class="lever-job" id="abc"><h2 class="lever-job-title">Relative href job</h2>
                <a class="lever-job-apply" href="/jobs/abc">Apply</a>
              </li>
            </ul>
            """;
        var jobs = _parser.Parse(html, "https://example.com/careers");
        Assert.Single(jobs);
        Assert.Equal("https://example.com/jobs/abc", jobs[0].Url);
    }

    [Fact]
    public void Parse_trims_whitespace_in_title()
    {
        // Real-world HTML often has newlines + indentation inside <h2>. Title should be cleaned.
        var html = """
            <ul class="lever">
              <li class="lever-job" id="xyz">
                <h2 class="lever-job-title">
                  Senior Engineer
                </h2>
                <a class="lever-job-apply" href="https://jobs.lever.co/foo/xyz">Apply</a>
              </li>
            </ul>
            """;
        var jobs = _parser.Parse(html, "https://example.com/");
        Assert.Single(jobs);
        Assert.Equal("Senior Engineer", jobs[0].Title);
    }
}
