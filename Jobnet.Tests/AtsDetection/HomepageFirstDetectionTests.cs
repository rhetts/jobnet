using Jobnet.Models;
using Jobnet.Services.AtsDetection;

namespace Jobnet.Tests.AtsDetection;

public class HomepageFirstDetectionTests
{
    private const string Home = "https://www.acme.com/";

    [Fact]
    public void ExtractCareersLinks_prefers_one_word_nav_link_and_resolves_relative()
    {
        const string html = """
            <a href="/about">About</a>
            <a href="/blog/we-are-growing">Our team is growing</a>
            <a href="/company/careers">Careers</a>
            """;
        var links = AtsDetector.ExtractCareersLinks(html, Home, "acme.com");
        Assert.Equal("https://www.acme.com/company/careers", links[0]);
    }

    [Fact]
    public void ExtractCareersLinks_matches_on_path_when_text_is_an_icon()
    {
        const string html = """<a href="/join-us"><img src="x.svg"></a>""";
        Assert.Equal(new[] { "https://www.acme.com/join-us" }, AtsDetector.ExtractCareersLinks(html, Home, "acme.com"));
    }

    [Fact]
    public void ExtractCareersLinks_keeps_careers_subdomain_and_drops_other_external_sites()
    {
        const string html = """
            <a href="https://careers.acme.com/">Jobs</a>
            <a href="https://www.linkedin.com/company/acme/jobs">Jobs on LinkedIn</a>
            <a href="https://jobs.careerbeacon.com/details/x/1">Careers</a>
            """;
        var links = AtsDetector.ExtractCareersLinks(html, Home, "acme.com");
        Assert.Contains("https://careers.acme.com/", links);
        Assert.DoesNotContain(links, l => l.Contains("linkedin"));
        Assert.DoesNotContain(links, l => l.Contains("careerbeacon"));
    }

    [Fact]
    public void ExtractCareersLinks_drops_careers_site_of_another_company()
    {
        const string html = """<a href="https://careers.nttdata.com/global/en">Careers</a>""";
        Assert.Empty(AtsDetector.ExtractCareersLinks(html, "https://www.apisero.com/", "apisero.com"));
    }

    [Theory]
    [InlineData("www.acme.com", "acme.com")]
    [InlineData("app.dealroom.co", "dealroom.co")]
    [InlineData("careers.acme.co.uk", "acme.co.uk")]
    [InlineData("acme.com.au", "acme.com.au")]
    public void RegistrableDomain_strips_subdomains(string host, string expected)
    {
        Assert.Equal(expected, AtsDetector.RegistrableDomain(host));
    }

    [Fact]
    public void ExtractCareersLinks_ignores_fragments_mail_and_self_links()
    {
        const string html = """
            <a href="#careers">Careers</a>
            <a href="mailto:jobs@acme.com">Jobs</a>
            <a href="https://www.acme.com/">Careers</a>
            """;
        Assert.Empty(AtsDetector.ExtractCareersLinks(html, Home, "acme.com"));
    }

    [Fact]
    public void ExtractCareersLinks_dedupes_and_caps_at_three()
    {
        const string html = """
            <a href="/careers">Careers</a><a href="/careers/">Careers</a>
            <a href="/jobs">Jobs</a><a href="/join">Join us</a><a href="/hiring">Hiring</a>
            """;
        var links = AtsDetector.ExtractCareersLinks(html, Home, "acme.com");
        Assert.Equal(3, links.Count);
        Assert.Single(links, l => l.TrimEnd('/').EndsWith("/careers"));
    }

    [Fact]
    public void ExtractCareersLinks_reads_french_careers_link()
    {
        const string html = """<a href="/fr/carrieres">Carrières</a>""";
        Assert.Single(AtsDetector.ExtractCareersLinks(html, Home, "acme.com"));
    }

    [Theory]
    [InlineData("https://www.dashsocial.com/", "dashhudson.com", true)]
    [InlineData("https://www.dashhudson.com/", "dashhudson.com", false)]
    [InlineData("https://careers.dashhudson.com/", "dashhudson.com", false)]
    [InlineData("https://dealroom.co/", "app.dealroom.co", false)]
    [InlineData("https://www.nttdata.com/en-us/about-us", "apisero.com", true)]
    public void MovedDomainNote_flags_redirect_to_another_domain(string finalUrl, string domain, bool moved)
    {
        var c = new Company { Id = 1, Name = "x", Domain = domain };
        Assert.Equal(moved, AtsDetector.MovedDomainNote(c, finalUrl) is not null);
    }
}
