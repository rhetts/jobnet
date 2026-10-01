using System.Text.Json;
using Jobnet.Services.JobSources;

namespace Jobnet.Tests.JobSources;

public class CornerstoneJobSourceTests
{
    // Trimmed from a real seequent.csod.com search response (2026-09-30).
    private const string SearchJson = """
        {
          "status": "Success",
          "data": {
            "totalCount": 2,
            "requisitions": [
              {
                "requisitionId": 4382,
                "displayJobTitle": "Senior AI Developer",
                "locations": [ { "city": "Vancouver", "state": "BC", "country": "CA" },
                               { "city": "Christchurch", "state": null, "country": "NZ" } ],
                "externalDescription": " Seequentâ€™s team Â· builds geoscience software. "
              },
              { "requisitionId": null, "displayJobTitle": "No id", "locations": [] },
              { "requisitionId": 4372, "displayJobTitle": "ENTER DISPLAY JOB TITLE",
                "locations": [ { "city": "Vancouver", "country": "CA" } ],
                "externalDescription": " Use Job Ad Template " }
            ]
          }
        }
        """;

    private static CornerstoneJobSource.Response Load() =>
        JsonSerializer.Deserialize<CornerstoneJobSource.Response>(SearchJson)!;

    [Fact]
    public void ParseResponse_maps_requisitions_and_skips_rows_without_id_or_placeholder_title()
    {
        var jobs = CornerstoneJobSource.ParseResponse(Load(), "seequent", 1);
        var job = Assert.Single(jobs);
        Assert.Equal("4382", job.NativeId);
        Assert.Equal("Senior AI Developer", job.Title);
        Assert.Equal("https://seequent.csod.com/ux/ats/careersite/1/home/requisition/4382?c=seequent", job.Url);
    }

    [Fact]
    public void ParseResponse_puts_extra_locations_in_secondary()
    {
        var job = CornerstoneJobSource.ParseResponse(Load(), "seequent", 1)[0];
        Assert.Equal("Vancouver, BC, CA", job.Location);
        Assert.Equal(new[] { "Christchurch, NZ" }, job.SecondaryLocations);
    }

    [Fact]
    public void ParseResponse_repairs_double_encoded_description()
    {
        var job = CornerstoneJobSource.ParseResponse(Load(), "seequent", 1)[0];
        Assert.Contains("Seequent’s team · builds", job.DescriptionSnippet);
    }

    [Fact]
    public void FixMojibake_leaves_clean_text_alone()
    {
        Assert.Equal("Café — Montréal", CornerstoneJobSource.FixMojibake("Café — Montréal"));
    }

    [Theory]
    [InlineData("seequent", "seequent", 1)]
    [InlineData("Seequent/3", "seequent", 3)]
    [InlineData("acme/notanumber", "acme", 1)]
    public void ParseSlug_splits_tenant_and_site(string slug, string tenant, int site)
    {
        Assert.Equal((tenant, site), CornerstoneJobSource.ParseSlug(slug));
    }

    [Fact]
    public void ExtractSession_reads_token_and_normalises_cloud()
    {
        var html = """<script>var ctx = {"cloud":"https://uk.api.csod.com","token":"eyJabc.def"};</script>""";
        var s = CornerstoneJobSource.ExtractSession(html);
        Assert.NotNull(s);
        Assert.Equal("eyJabc.def", s!.Value.Token);
        Assert.Equal("https://uk.api.csod.com/", s.Value.Cloud);
    }

    [Fact]
    public void ExtractSession_returns_null_without_token()
    {
        Assert.Null(CornerstoneJobSource.ExtractSession("<html>no context here</html>"));
    }
}
