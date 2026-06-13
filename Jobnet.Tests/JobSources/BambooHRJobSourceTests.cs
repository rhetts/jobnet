using System.IO;
using System.Linq;
using System.Text.Json;
using Jobnet.Services.JobSources;
using Jobnet.Tests.Fixtures;

namespace Jobnet.Tests.JobSources;

public class BambooHRJobSourceTests
{
    private static BambooHRJobSource.Response LoadFixture(string name)
    {
        var raw = FixtureLoader.Load(name);
        return JsonSerializer.Deserialize<BambooHRJobSource.Response>(raw)
               ?? throw new InvalidDataException("fixture deserialised to null");
    }

    [Fact]
    public void ParseResponse_extracts_all_postings_from_itblueprint_fixture()
    {
        var response = LoadFixture("bamboohr-itblueprint.json");
        var jobs = BambooHRJobSource.ParseResponse(response, "itblueprint");
        Assert.Equal(3, jobs.Count);
    }

    [Fact]
    public void ParseResponse_builds_canonical_job_urls_from_slug_and_id()
    {
        var response = LoadFixture("bamboohr-itblueprint.json");
        var jobs = BambooHRJobSource.ParseResponse(response, "itblueprint");
        var first = jobs.Single(j => j.NativeId == "18");
        Assert.Equal("https://itblueprint.bamboohr.com/careers/18", first.Url);
    }

    [Fact]
    public void ParseResponse_formats_city_state_as_location()
    {
        var response = LoadFixture("bamboohr-itblueprint.json");
        var jobs = BambooHRJobSource.ParseResponse(response, "itblueprint");
        Assert.Equal("Vancouver, British Columbia", jobs.Single(j => j.NativeId == "18").Location);
    }

    [Fact]
    public void ParseResponse_maps_locationType_to_remote_enum()
    {
        // 2 = on-site, 3 = remote, 4 = hybrid in BambooHR's locationType taxonomy.
        var response = LoadFixture("bamboohr-itblueprint.json");
        var jobs = BambooHRJobSource.ParseResponse(response, "itblueprint");
        Assert.Equal("on-site", jobs.Single(j => j.NativeId == "18").RemoteType);
        Assert.Equal("remote",  jobs.Single(j => j.NativeId == "21").RemoteType);
        Assert.Equal("hybrid",  jobs.Single(j => j.NativeId == "33").RemoteType);
    }

    [Fact]
    public void ParseResponse_isRemote_true_overrides_locationType()
    {
        // The Senior Cloud Architect entry has both isRemote=true and locationType="3".
        // The explicit flag should win even when the locationType disagrees.
        var response = LoadFixture("bamboohr-itblueprint.json");
        var jobs = BambooHRJobSource.ParseResponse(response, "itblueprint");
        Assert.Equal("remote", jobs.Single(j => j.NativeId == "21").RemoteType);
    }

    [Fact]
    public void ParseResponse_normalizes_employment_label()
    {
        var response = LoadFixture("bamboohr-itblueprint.json");
        var jobs = BambooHRJobSource.ParseResponse(response, "itblueprint");
        Assert.Equal("full-time", jobs.Single(j => j.NativeId == "18").EmploymentType);
        Assert.Equal("full-time", jobs.Single(j => j.NativeId == "21").EmploymentType);
        Assert.Equal("part-time", jobs.Single(j => j.NativeId == "33").EmploymentType);
    }

    [Fact]
    public void ParseResponse_returns_empty_when_payload_is_null()
    {
        Assert.Empty(BambooHRJobSource.ParseResponse(null, "itblueprint"));
    }

    [Fact]
    public void ParseResponse_skips_entries_missing_required_fields()
    {
        // Inline JSON with a missing title and a missing id — both should be silently dropped.
        var raw = "{\"result\": [" +
                  "{\"id\": \"5\"}," +                                            // no title
                  "{\"jobOpeningName\": \"Orphaned\"}," +                         // no id
                  "{\"id\": \"7\", \"jobOpeningName\": \"Valid\"}]}";
        var payload = JsonSerializer.Deserialize<BambooHRJobSource.Response>(raw);
        var jobs = BambooHRJobSource.ParseResponse(payload, "test");
        Assert.Single(jobs);
        Assert.Equal("7", jobs[0].NativeId);
    }
}
