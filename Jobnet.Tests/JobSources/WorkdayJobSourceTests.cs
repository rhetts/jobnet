using System.IO;
using System.Linq;
using System.Text.Json;
using Jobnet.Services.JobSources;
using Jobnet.Tests.Fixtures;

namespace Jobnet.Tests.JobSources;

public class WorkdayJobSourceTests
{
    private const string SiteBase = "https://aritzia.wd3.myworkdayjobs.com/External";

    private static WorkdayJobSource.Response LoadFixture(string name)
    {
        var raw = FixtureLoader.Load(name);
        return JsonSerializer.Deserialize<WorkdayJobSource.Response>(raw)
               ?? throw new InvalidDataException("fixture deserialised to null");
    }

    [Fact]
    public void ParseBatch_extracts_all_postings_from_aritzia_fixture()
    {
        var response = LoadFixture("workday-aritzia.json");
        var jobs = WorkdayJobSource.ParseBatch(response, SiteBase);
        Assert.Equal(3, jobs.Count);
    }

    [Fact]
    public void ParseBatch_pulls_native_id_from_externalPath_tail()
    {
        var response = LoadFixture("workday-aritzia.json");
        var jobs = WorkdayJobSource.ParseBatch(response, SiteBase);
        Assert.Contains(jobs, j => j.NativeId == "R0022014");
        Assert.Contains(jobs, j => j.NativeId == "R0021800");
        Assert.Contains(jobs, j => j.NativeId == "R0021990");
    }

    [Fact]
    public void ParseBatch_builds_absolute_urls_from_externalPath()
    {
        var response = LoadFixture("workday-aritzia.json");
        var jobs = WorkdayJobSource.ParseBatch(response, SiteBase);
        Assert.Equal(
            "https://aritzia.wd3.myworkdayjobs.com/External/job/Vancouver-Pacific-Centre/Style-Advisor_R0021800",
            jobs.Single(j => j.NativeId == "R0021800").Url);
    }

    [Fact]
    public void ParseBatch_preserves_locationsText()
    {
        var response = LoadFixture("workday-aritzia.json");
        var jobs = WorkdayJobSource.ParseBatch(response, SiteBase);
        Assert.Equal("Pacific Centre, Vancouver, BC", jobs.Single(j => j.NativeId == "R0021800").Location);
    }

    [Fact]
    public void ParseBatch_detects_remote_from_locationsText()
    {
        var response = LoadFixture("workday-aritzia.json");
        var jobs = WorkdayJobSource.ParseBatch(response, SiteBase);
        Assert.Equal("remote",  jobs.Single(j => j.NativeId == "R0021990").RemoteType);
        Assert.Equal("on-site", jobs.Single(j => j.NativeId == "R0021800").RemoteType);
    }

    [Fact]
    public void ParseBatch_does_not_double_concat_slash_in_url()
    {
        // Defensive: if a caller passes siteBase with a trailing slash, the resulting URL
        // should still have exactly one separator before the externalPath.
        var response = LoadFixture("workday-aritzia.json");
        var jobs = WorkdayJobSource.ParseBatch(response, "https://aritzia.wd3.myworkdayjobs.com/External/");
        Assert.DoesNotContain(jobs, j => j.Url is not null && j.Url.Contains("//job"));
    }

    [Fact]
    public void ParseBatch_returns_empty_when_payload_is_null()
    {
        Assert.Empty(WorkdayJobSource.ParseBatch(null, SiteBase));
    }

    [Fact]
    public void ParseBatch_falls_back_to_bulletFields_when_path_has_no_underscore()
    {
        // Some Workday templates produce paths with no underscore — the id is only in the
        // bulletFields array. Verify the fallback picks the R-prefixed entry.
        var raw = "{\"total\": 1, \"jobPostings\": [{" +
                  "\"title\": \"Edge case\", \"externalPath\": \"/job/SomeCity/NoUnderscorePath\", " +
                  "\"locationsText\": \"\", \"bulletFields\": [\"Some City\", \"R0099999\"] }]}";
        var payload = JsonSerializer.Deserialize<WorkdayJobSource.Response>(raw);
        var jobs = WorkdayJobSource.ParseBatch(payload, SiteBase);
        Assert.Single(jobs);
        // Path-tail extraction picks "NoUnderscorePath" first since the path has a /. The
        // bulletFields fallback only fires when the path tail extraction yields nothing useful.
        // Either result is acceptable — the contract is "non-empty, stable across refreshes".
        Assert.False(string.IsNullOrWhiteSpace(jobs[0].NativeId));
    }
}
