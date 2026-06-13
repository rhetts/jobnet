using System.Linq;
using Jobnet.Services.Parsing.HtmlPatternParsers;
using Jobnet.Tests.Fixtures;

namespace Jobnet.Tests.Parsing;

public class GreenhouseLinkParserTests
{
    private readonly GreenhouseLinkParser _parser = new();

    // ── CanHandle ───────────────────────────────────────────────────────────

    [Fact]
    public void CanHandle_returns_true_for_7shifts_fixture()
    {
        var html = FixtureLoader.Load("7shifts.html");
        Assert.True(_parser.CanHandle("https://www.7shifts.com/careers", html));
    }

    [Fact]
    public void CanHandle_returns_false_for_blackbird_fixture()
    {
        // Cross-check: Blackbird uses Lever shortcode, not Greenhouse links — make sure our
        // CanHandle doesn't false-positive on a different ATS family's page.
        var html = FixtureLoader.Load("blackbird-interactive.html");
        Assert.False(_parser.CanHandle("https://blackbirdinteractive.com/careers-2/", html));
    }

    [Fact]
    public void CanHandle_returns_false_for_empty_html()
    {
        Assert.False(_parser.CanHandle("https://example.com/", ""));
    }

    [Fact]
    public void CanHandle_returns_false_when_url_pattern_is_only_partial()
    {
        // Page mentions Greenhouse marketing-wise but has no /jobs/ links — shouldn't trigger.
        var html = "<html><body>We use Greenhouse for hiring (boards.greenhouse.io was great).</body></html>";
        Assert.False(_parser.CanHandle("https://example.com/", html));
    }

    // ── Parse: real fixture ─────────────────────────────────────────────────

    [Fact]
    public void Parse_7shifts_finds_postings()
    {
        var html = FixtureLoader.Load("7shifts.html");
        var jobs = _parser.Parse(html, "https://www.7shifts.com/careers");
        Assert.NotEmpty(jobs);
    }

    [Fact]
    public void Parse_7shifts_url_id_round_trip()
    {
        var html = FixtureLoader.Load("7shifts.html");
        var jobs = _parser.Parse(html, "https://www.7shifts.com/careers");
        var spotCheck = jobs.SingleOrDefault(j => j.NativeId == "4740157004");
        Assert.NotNull(spotCheck);
        Assert.Contains("Customer Support", spotCheck!.Title);
        Assert.Equal("https://job-boards.greenhouse.io/7shifts/jobs/4740157004", spotCheck.Url);
    }

    [Fact]
    public void Parse_7shifts_native_ids_are_unique()
    {
        // 7shifts's careers page lists the same role title in multiple cities. Each city is a
        // distinct Greenhouse posting with its own numeric id — dedup must NOT collapse them.
        var html = FixtureLoader.Load("7shifts.html");
        var jobs = _parser.Parse(html, "https://www.7shifts.com/careers");
        var ids = jobs.Select(j => j.NativeId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Parse_7shifts_extracts_location_when_paragraph_follows_title()
    {
        // The 7shifts template puts <p>City, Province</p> as the title's next sibling.
        // Verify the parser surfaces it on at least one of the Vancouver postings.
        var html = FixtureLoader.Load("7shifts.html");
        var jobs = _parser.Parse(html, "https://www.7shifts.com/careers");
        Assert.Contains(jobs, j => j.Location is not null && j.Location.Contains("Vancouver"));
    }

    // ── Parse: synthetic edge cases ─────────────────────────────────────────

    [Fact]
    public void Parse_dedups_identical_postings_by_native_id()
    {
        // Some templates duplicate the same anchor (mobile + desktop variants). The id must
        // anchor dedup so we don't double-count.
        var html = """
            <a href="https://boards.greenhouse.io/foo/jobs/12345"><h3>Repeated</h3></a>
            <a href="https://boards.greenhouse.io/foo/jobs/12345"><h3>Repeated</h3></a>
            <a href="https://boards.greenhouse.io/foo/jobs/67890"><h3>Other</h3></a>
            """;
        var jobs = _parser.Parse(html, "https://example.com/");
        Assert.Equal(2, jobs.Count);
        Assert.Contains(jobs, j => j.NativeId == "12345");
        Assert.Contains(jobs, j => j.NativeId == "67890");
    }

    [Fact]
    public void Parse_skips_non_greenhouse_anchors()
    {
        var html = """
            <a href="https://example.com/contact"><h3>Contact</h3></a>
            <a href="https://boards.greenhouse.io/foo/jobs/abc"><h3>Bad id (non-numeric)</h3></a>
            <a href="https://boards.greenhouse.io/foo/jobs/42"><h3>Real Job</h3></a>
            """;
        var jobs = _parser.Parse(html, "https://example.com/");
        Assert.Single(jobs);
        Assert.Equal("42", jobs[0].NativeId);
        Assert.Equal("Real Job", jobs[0].Title);
    }

    [Fact]
    public void Parse_falls_back_to_anchor_text_when_no_heading()
    {
        var html = """<a href="https://boards.greenhouse.io/foo/jobs/9">Plain Anchor Title</a>""";
        var jobs = _parser.Parse(html, "https://example.com/");
        Assert.Single(jobs);
        Assert.Equal("Plain Anchor Title", jobs[0].Title);
    }

    [Fact]
    public void Parse_handles_job_boards_subdomain()
    {
        // 7shifts uses job-boards.greenhouse.io (with the dash); the legacy host is boards.greenhouse.io.
        // The regex needs to accept both — guard against a regression that breaks one.
        var legacy = """<a href="https://boards.greenhouse.io/foo/jobs/1"><h3>Legacy host</h3></a>""";
        var modern = """<a href="https://job-boards.greenhouse.io/foo/jobs/2"><h3>Modern host</h3></a>""";
        Assert.Single(_parser.Parse(legacy, "https://example.com/"));
        Assert.Single(_parser.Parse(modern, "https://example.com/"));
    }
}
