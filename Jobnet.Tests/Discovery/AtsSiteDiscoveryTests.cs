using Jobnet.Services.AtsDetection;
using Jobnet.Services.Discovery;

namespace Jobnet.Tests.Discovery;

public class AtsSiteDiscoveryTests
{
    [Theory]
    [InlineData("https://jobs.ashbyhq.com/klue/3f2a9c1e-0000-4000-8000-000000000000", "ashby", "klue", "https://jobs.ashbyhq.com/klue")]
    [InlineData("https://job-boards.greenhouse.io/hootsuite/jobs/6543210", "greenhouse", "hootsuite", "https://job-boards.greenhouse.io/hootsuite")]
    [InlineData("https://boards.greenhouse.io/coinbase/jobs/123", "greenhouse", "coinbase", "https://boards.greenhouse.io/coinbase")]
    [InlineData("https://jobs.lever.co/skyboxlabs/0b1c2d3e-aaaa-bbbb-cccc-1234567890ab/apply", "lever", "skyboxlabs", "https://jobs.lever.co/skyboxlabs")]
    [InlineData("https://apply.workable.com/humi/j/ABC123DEF/", "workable", "humi", "https://apply.workable.com/humi")]
    [InlineData("https://gasketgames.bamboohr.com/careers/42", "bamboohr", "gasketgames", "https://gasketgames.bamboohr.com")]
    [InlineData("https://careers.smartrecruiters.com/Leger2", "smartrecruiters", "leger2", "https://careers.smartrecruiters.com/Leger2")]
    [InlineData("https://aritzia.wd3.myworkdayjobs.com/External/job/Vancouver-BC/Analyst_R123", "workday", "aritzia.wd3.myworkdayjobs.com/External", "https://aritzia.wd3.myworkdayjobs.com/External")]
    public void TryMatchAtsUrl_extracts_ats_type_slug_and_board_url(string url, string ats, string slug, string board)
    {
        Assert.True(AtsDetector.TryMatchAtsUrl(url, out var atsType, out var atsSlug, out var boardUrl));
        Assert.Equal(ats, atsType);
        Assert.Equal(slug, atsSlug);
        Assert.Equal(board, boardUrl);
    }

    [Fact]
    public void TryMatchAtsUrl_skips_workday_locale_segment()
    {
        Assert.True(AtsDetector.TryMatchAtsUrl(
            "https://clio.wd3.myworkdayjobs.com/en-US/ClioCareerSite/job/Vancouver/Engineer_R1", out var ats, out var slug));
        Assert.Equal("workday", ats);
        Assert.Equal("clio.wd3.myworkdayjobs.com/ClioCareerSite", slug);
    }

    [Fact]
    public void TryMatchAtsUrl_cornerstone_site_one_is_bare_tenant_with_loadable_board_url()
    {
        Assert.True(AtsDetector.TryMatchAtsUrl(
            "https://seequent.csod.com/ux/ats/careersite/1/home/requisition/4382?c=seequent",
            out var ats, out var slug, out var board));
        Assert.Equal("cornerstone", ats);
        Assert.Equal("seequent", slug);
        Assert.Equal("https://seequent.csod.com/ux/ats/careersite/1/home?c=seequent", board);
    }

    [Fact]
    public void TryMatchAtsUrl_cornerstone_other_site_keeps_site_id()
    {
        Assert.True(AtsDetector.TryMatchAtsUrl(
            "https://acme.csod.com/ux/ats/careersite/5/home?c=acme", out var ats, out var slug));
        Assert.Equal("cornerstone", ats);
        Assert.Equal("acme/5", slug);
    }

    [Theory]
    [InlineData("https://klue.com/careers")]
    [InlineData("https://www.bamboohr.com/pricing")]
    [InlineData("https://www.workable.com/")]
    [InlineData("https://boards.greenhouse.io/embed/job_board/js")]
    [InlineData("")]
    public void TryMatchAtsUrl_rejects_non_board_urls(string url)
    {
        Assert.False(AtsDetector.TryMatchAtsUrl(url, out _, out _));
    }

    [Theory]
    [InlineData("klue", "klue.com", true)]
    [InlineData("boldr-1", "boldrimpact.com", true)]
    [InlineData("leger2", "leger360.com", true)]
    [InlineData("gravite", "gravit-e.ca", true)]
    [InlineData("1password", "1password.com", true)]
    [InlineData("aritzia.wd3.myworkdayjobs.com/External", "aritzia.com", true)]
    [InlineData("seequent/5", "seequent.com", true)]
    [InlineData("acme", "acme.co.uk", true)]
    [InlineData("klue", "ashbyprd.com", false)]
    [InlineData("skyboxlabs", "w3.org", false)]
    [InlineData("ab", "ab.com", false)]
    public void SlugMatchesDomain_compares_letters_of_tenant_and_second_level_label(string slug, string domain, bool expected)
    {
        Assert.Equal(expected, AtsBoardDomainResolver.SlugMatchesDomain(slug, domain));
    }

    [Fact]
    public void PickCompanyDomain_skips_vendor_and_unrelated_hosts()
    {
        // Shape of a real jobs.ashbyhq.com/klue page: vendor CDN/app links first, the company
        // site only inside script data.
        const string html = """
            <link href="https://cdn.ashbyprd.com/frontend_non_user/app.css">
            <script src="https://app.ashbyhq.com/bundle.js"></script>
            <a href="https://www.w3.org/2000/svg">x</a>
            <script>window.__appData = {"organization":{"name":"Klue","publicWebsite":"https://klue.com"}}</script>
            """;
        Assert.Equal("klue.com",
            AtsBoardDomainResolver.PickCompanyDomain(html, "https://jobs.ashbyhq.com/klue", "klue", _ => false));
    }

    [Fact]
    public void PickCompanyDomain_strips_www_and_reads_json_escaped_urls()
    {
        const string html = """
            <a href="https://www.lever.co/">Powered by Lever</a>
            <script>var d = {"site":"http:\/\/www.skyboxlabs.com\/"};</script>
            """;
        Assert.Equal("skyboxlabs.com",
            AtsBoardDomainResolver.PickCompanyDomain(html, "https://jobs.lever.co/skyboxlabs", "skyboxlabs", _ => false));
    }

    [Fact]
    public void PickCompanyDomain_respects_blocked_predicate()
    {
        const string html = """<a href="https://klue.com">Klue</a>""";
        Assert.Null(AtsBoardDomainResolver.PickCompanyDomain(
            html, "https://jobs.ashbyhq.com/klue", "klue", d => d == "klue.com"));
    }

    [Fact]
    public void PickCompanyDomain_returns_null_when_nothing_matches_slug()
    {
        const string html = """<a href="https://example.com">x</a><a href="https://job-boards.greenhouse.io/hootsuite">y</a>""";
        Assert.Null(AtsBoardDomainResolver.PickCompanyDomain(
            html, "https://job-boards.greenhouse.io/hootsuite", "hootsuite", _ => false));
    }

    [Theory]
    [InlineData("""<a href="https://job-boards.greenhouse.io/cclfg">Jobs</a>""", true)]
    [InlineData("""<script src="https://boards.greenhouse.io/embed/job_board/js?for=cclfg"></script>""", true)]
    [InlineData("""fetch("https://boards-api.greenhouse.io/v1/boards/cclfg/jobs")""", true)]
    [InlineData("""<a href="https://job-boards.greenhouse.io/cclfg2">Jobs</a>""", false)]
    [InlineData("""<p>cclfg</p>""", false)]
    public void PageLinksBoard_greenhouse_requires_this_exact_board(string html, bool expected)
    {
        Assert.Equal(expected, AtsBoardDomainResolver.PageLinksBoard(html, "greenhouse", "cclfg"));
    }

    [Theory]
    [InlineData("""<a href="https://autodesk.wd1.myworkdayjobs.com/Ext">Careers</a>""", true)]
    [InlineData("""<a href="https://stryker.wd1.myworkdayjobs.com/StrykerCareers">x</a>""", false)]
    public void PageLinksBoard_workday_matches_tenant_host(string html, bool expected)
    {
        Assert.Equal(expected, AtsBoardDomainResolver.PageLinksBoard(
            html, "workday", "autodesk.wd1.myworkdayjobs.com/Ext"));
    }

    [Fact]
    public void PageLinksBoard_other_ats_types_never_match()
    {
        Assert.False(AtsBoardDomainResolver.PageLinksBoard(
            """<a href="https://jobs.lever.co/acme">x</a>""", "lever", "acme"));
    }
}
