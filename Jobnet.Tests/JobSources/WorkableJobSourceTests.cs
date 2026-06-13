using System.IO;
using System.Linq;
using System.Text.Json;
using Jobnet.Services.JobSources;
using Jobnet.Tests.Fixtures;

namespace Jobnet.Tests.JobSources;

public class WorkableJobSourceTests
{
    private static WorkableJobSource.Response LoadFixture(string name)
    {
        var raw = FixtureLoader.Load(name);
        return JsonSerializer.Deserialize<WorkableJobSource.Response>(raw)
               ?? throw new InvalidDataException("fixture deserialised to null");
    }

    [Fact]
    public void ParseResponse_extracts_all_postings_from_mercari_fixture()
    {
        var response = LoadFixture("workable-mercari.json");
        var jobs = WorkableJobSource.ParseResponse(response);
        Assert.Equal(3, jobs.Count);
    }

    [Fact]
    public void ParseResponse_uses_shortcode_as_native_id()
    {
        var response = LoadFixture("workable-mercari.json");
        var jobs = WorkableJobSource.ParseResponse(response);
        Assert.All(jobs, j => Assert.False(string.IsNullOrWhiteSpace(j.NativeId)));
        // Shortcodes are ALL-CAPS alphanumeric, ~10 chars — sanity-check at least one matches.
        Assert.Contains(jobs, j => j.NativeId.Length >= 8 && j.NativeId.All(char.IsLetterOrDigit));
    }

    [Fact]
    public void ParseResponse_uses_workable_url_field_when_present()
    {
        var response = LoadFixture("workable-mercari.json");
        var jobs = WorkableJobSource.ParseResponse(response);
        Assert.All(jobs, j =>
        {
            Assert.NotNull(j.Url);
            Assert.StartsWith("https://apply.workable.com/", j.Url);
        });
    }

    [Fact]
    public void ParseResponse_falls_back_to_constructed_url_when_url_missing()
    {
        var raw = "{\"jobs\":[{\"shortcode\":\"ABC123\",\"title\":\"X\"}]}";
        var payload = JsonSerializer.Deserialize<WorkableJobSource.Response>(raw);
        var jobs = WorkableJobSource.ParseResponse(payload);
        Assert.Single(jobs);
        Assert.Equal("https://apply.workable.com/j/ABC123", jobs[0].Url);
    }

    [Fact]
    public void ParseResponse_telecommuting_true_maps_to_remote()
    {
        var raw = "{\"jobs\":[{\"shortcode\":\"X\",\"title\":\"R\",\"telecommuting\":true}]}";
        var payload = JsonSerializer.Deserialize<WorkableJobSource.Response>(raw);
        Assert.Equal("remote", WorkableJobSource.ParseResponse(payload)[0].RemoteType);
    }

    [Fact]
    public void ParseResponse_prefers_locations_array_over_flat_fields()
    {
        // When both are present, the structured locations[] entry wins. Test passes both and
        // confirms the structured form is what comes out.
        var raw = """
            { "jobs": [{ "shortcode":"X", "title":"T",
                          "city":"FlatCity", "state":"FlatState", "country":"FlatCountry",
                          "locations":[{"city":"StructCity","region":"StructRegion","country":"StructCountry"}] }] }
            """;
        var payload = JsonSerializer.Deserialize<WorkableJobSource.Response>(raw);
        var loc = WorkableJobSource.ParseResponse(payload)[0].Location;
        Assert.Equal("StructCity, StructRegion, StructCountry", loc);
    }

    [Fact]
    public void ParseResponse_normalizes_employment_label()
    {
        var raw = "{\"jobs\":[" +
                  "{\"shortcode\":\"A\",\"title\":\"X\",\"employment_type\":\"Full-time\"}," +
                  "{\"shortcode\":\"B\",\"title\":\"Y\",\"employment_type\":\"Part-time\"}," +
                  "{\"shortcode\":\"C\",\"title\":\"Z\",\"employment_type\":\"Temporary\"} ]}";
        var payload = JsonSerializer.Deserialize<WorkableJobSource.Response>(raw);
        var jobs = WorkableJobSource.ParseResponse(payload);
        Assert.Equal("full-time", jobs.Single(j => j.NativeId == "A").EmploymentType);
        Assert.Equal("part-time", jobs.Single(j => j.NativeId == "B").EmploymentType);
        Assert.Equal("contract",  jobs.Single(j => j.NativeId == "C").EmploymentType);
    }

    [Fact]
    public void ParseResponse_returns_empty_when_payload_is_null()
    {
        Assert.Empty(WorkableJobSource.ParseResponse(null));
    }

    [Fact]
    public void ParseResponse_skips_entries_missing_required_fields()
    {
        var raw = "{\"jobs\":[" +
                  "{\"shortcode\":\"\",\"title\":\"NoCode\"}," +
                  "{\"shortcode\":\"X\",\"title\":\"\"}," +
                  "{\"shortcode\":\"OK\",\"title\":\"Valid\"} ]}";
        var payload = JsonSerializer.Deserialize<WorkableJobSource.Response>(raw);
        var jobs = WorkableJobSource.ParseResponse(payload);
        Assert.Single(jobs);
        Assert.Equal("OK", jobs[0].NativeId);
    }
}
