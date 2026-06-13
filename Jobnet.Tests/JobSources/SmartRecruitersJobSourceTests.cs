using System.IO;
using System.Linq;
using System.Text.Json;
using Jobnet.Services.JobSources;
using Jobnet.Tests.Fixtures;

namespace Jobnet.Tests.JobSources;

public class SmartRecruitersJobSourceTests
{
    private const string Slug = "Visa";

    private static SmartRecruitersJobSource.Response LoadFixture(string name)
    {
        var raw = FixtureLoader.Load(name);
        return JsonSerializer.Deserialize<SmartRecruitersJobSource.Response>(raw)
               ?? throw new InvalidDataException("fixture deserialised to null");
    }

    [Fact]
    public void ParseBatch_extracts_all_postings_from_visa_fixture()
    {
        var response = LoadFixture("smartrecruiters-visa.json");
        var jobs = SmartRecruitersJobSource.ParseBatch(response, Slug);
        Assert.Equal(3, jobs.Count);
    }

    [Fact]
    public void ParseBatch_builds_job_urls_from_slug_and_id()
    {
        var response = LoadFixture("smartrecruiters-visa.json");
        var jobs = SmartRecruitersJobSource.ParseBatch(response, Slug);
        Assert.All(jobs, j =>
        {
            Assert.NotNull(j.Url);
            Assert.StartsWith("https://jobs.smartrecruiters.com/Visa/", j.Url);
            Assert.EndsWith(j.NativeId, j.Url);
        });
    }

    [Fact]
    public void ParseBatch_prefers_fullLocation_when_present()
    {
        var response = LoadFixture("smartrecruiters-visa.json");
        var jobs = SmartRecruitersJobSource.ParseBatch(response, Slug);
        // Every Visa posting in the fixture has a fullLocation field set.
        Assert.All(jobs, j => Assert.False(string.IsNullOrWhiteSpace(j.Location)));
    }

    [Fact]
    public void ParseBatch_synthesises_location_from_parts_when_fullLocation_missing()
    {
        var raw = """
            {"content":[{"id":"1","name":"T","location":{"city":"Vancouver","region":"BC","country":"ca","remote":false,"hybrid":false}}]}
            """;
        var payload = JsonSerializer.Deserialize<SmartRecruitersJobSource.Response>(raw);
        var jobs = SmartRecruitersJobSource.ParseBatch(payload, Slug);
        Assert.Equal("Vancouver, BC, ca", jobs[0].Location);
    }

    [Fact]
    public void ParseBatch_remote_field_maps_to_remote_type()
    {
        var raw = """
            {"content":[
              {"id":"A","name":"a","location":{"city":"X","country":"us","remote":true,"hybrid":false}},
              {"id":"B","name":"b","location":{"city":"Y","country":"us","remote":false,"hybrid":true}},
              {"id":"C","name":"c","location":{"city":"Z","country":"us","remote":false,"hybrid":false}}
            ]}
            """;
        var payload = JsonSerializer.Deserialize<SmartRecruitersJobSource.Response>(raw);
        var jobs = SmartRecruitersJobSource.ParseBatch(payload, Slug);
        Assert.Equal("remote",  jobs.Single(j => j.NativeId == "A").RemoteType);
        Assert.Equal("hybrid",  jobs.Single(j => j.NativeId == "B").RemoteType);
        Assert.Equal("on-site", jobs.Single(j => j.NativeId == "C").RemoteType);
    }

    [Fact]
    public void ParseBatch_extracts_department_and_employment_labels()
    {
        var response = LoadFixture("smartrecruiters-visa.json");
        var jobs = SmartRecruitersJobSource.ParseBatch(response, Slug);
        // Every Visa posting has a department.label and typeOfEmployment.label set.
        Assert.All(jobs, j =>
        {
            Assert.False(string.IsNullOrWhiteSpace(j.Department));
            // typeOfEmployment is "Full-time" in the fixture; normalize confirms it maps right.
            Assert.NotEqual("unknown", j.EmploymentType);
        });
    }

    [Fact]
    public void ParseBatch_returns_empty_when_payload_is_null()
    {
        Assert.Empty(SmartRecruitersJobSource.ParseBatch(null, Slug));
    }

    [Fact]
    public void ParseBatch_skips_entries_missing_required_fields()
    {
        var raw = """
            {"content":[
              {"id":"","name":"NoId"},
              {"id":"X","name":""},
              {"id":"OK","name":"Valid","location":{"city":"X","country":"us"}}
            ]}
            """;
        var payload = JsonSerializer.Deserialize<SmartRecruitersJobSource.Response>(raw);
        var jobs = SmartRecruitersJobSource.ParseBatch(payload, Slug);
        Assert.Single(jobs);
        Assert.Equal("OK", jobs[0].NativeId);
    }
}
