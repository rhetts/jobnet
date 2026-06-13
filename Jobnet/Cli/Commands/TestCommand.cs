using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Jobnet.Data;
using Jobnet.Services.Classification;
using Jobnet.Services.Discovery;
using Jobnet.Services.RateLimit;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

/// <summary>
/// Self-test command. Runs assertions on classifier, DomainExtractor, rate limiter,
/// and DB migrations. Exits 0 on full pass, 1 on any failure.
/// </summary>
public sealed class TestCommand : ICliCommand
{
    public string Name => "test";
    public string Description => "Run the built-in test suite. Exits 0 on full pass, 1 on any failure.";

    private int _pass;
    private int _fail;

    public int Run(string[] args, IServiceProvider services)
    {
        Console.WriteLine("Jobnet self-tests");
        Console.WriteLine("=================");
        Console.WriteLine();

        RunClassifierTests(services);
        RunDomainExtractorTests();
        RunUrlClassifierTests();
        RunLocationMatcherTests();
        RunRateLimiterTests(services).GetAwaiter().GetResult();
        RunMigrationTests(services);

        Console.WriteLine();
        Console.WriteLine($"{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    private void RunClassifierTests(IServiceProvider services)
    {
        Console.WriteLine("Classifier tests:");
        var c = services.GetRequiredService<IJobClassifier>();

        AssertClassify(c, "Senior Backend Engineer",      "Senior",            "Software Engineering");
        AssertClassify(c, "Staff iOS Engineer",           "Staff / Principal", "Software Engineering");
        AssertClassify(c, "Lead Engineer (Backend)",      "Lead",              "Software Engineering");
        AssertClassify(c, "Junior Data Analyst",          "Junior",            "Data / ML");
        AssertClassify(c, "Director of Engineering",      "Director",          "Management");
        AssertClassify(c, "Software Developer III",       "Senior",            "Software Engineering");
        AssertClassify(c, "Frontend Developer",           "Mid",               "Software Engineering");
        AssertClassify(c, "Founding Engineer",            "Staff / Principal", "Software Engineering");
        AssertClassify(c, "DevOps Engineer",              "Mid",               "DevOps / Platform");
        AssertClassify(c, "ML Engineer, Search",          "Mid",               "Data / ML");
        AssertClassify(c, "Security Engineer",            "Mid",               "Security");
        AssertClassify(c, "QA Automation Engineer",       "Mid",               "QA / Test");
        AssertClassify(c, "Product Manager",              "Manager",           "Product Management");
        AssertClassify(c, "Bookkeeper",                   null,                "Finance");
        Console.WriteLine();
    }

    private void AssertClassify(IJobClassifier c, string title, string? expectedLevel, string expectedAreaContains)
    {
        var r = c.Classify(title);
        var levelOk = expectedLevel is null ? r.LevelName is null : string.Equals(r.LevelName, expectedLevel, StringComparison.OrdinalIgnoreCase);
        var areaOk  = r.Areas.Any(a => string.Equals(a.Name, expectedAreaContains, StringComparison.OrdinalIgnoreCase));

        var ok = levelOk && areaOk;
        var msg = $"\"{title}\" → level={r.LevelName ?? "(none)"} areas=[{string.Join(",", r.Areas.Select(a => a.Name))}]";
        if (ok) Pass(msg);
        else    Fail(msg + $"  (expected level={expectedLevel ?? "(none)"}, area containing {expectedAreaContains})");
    }

    private void RunDomainExtractorTests()
    {
        Console.WriteLine("DomainExtractor tests:");
        // Skip cases
        AssertSkipped("https://www.linkedin.com/in/somebody",               true);
        AssertSkipped("https://ca.indeed.com/jobs?q=engineer",              true);
        AssertSkipped("https://glassdoor.ca/Job/x",                         true);
        AssertSkipped("https://www.crunchbase.com/organization/acme",       true);
        AssertSkipped("https://www.bcsc.bc.ca/news/some-article",           true);  // suffix match bug fix
        AssertSkipped("https://bcsc.bc.ca/news/some-article",               true);
        AssertSkipped("https://www.clutch.co/profile/x",                    true);
        AssertSkipped("https://startus-insights.com/innovators/x",          true);
        AssertSkipped("https://en.wikipedia.org/wiki/Acme",                 true);

        // Keep cases
        AssertSkipped("https://www.steamclock.com",                         false);
        AssertSkipped("https://blackbirdinteractive.com/about",             false);
        AssertSkipped("https://acme.com",                                   false);
        AssertSkipped("https://boards.greenhouse.io/shopify",               false);  // ATS link is a valid signal

        // Canonical-domain stripping
        AssertCanonical("https://www.acme.com",       "acme.com");
        AssertCanonical("https://careers.acme.com/x", "acme.com");
        AssertCanonical("https://jobs.acme.com/x",    "acme.com");
        Console.WriteLine();
    }

    private void AssertSkipped(string url, bool shouldBeSkipped)
    {
        var r = DomainExtractor.Extract(url);
        var ok = (r is null) == shouldBeSkipped;
        if (ok) Pass($"{url} → {(shouldBeSkipped ? "skipped" : "kept")}");
        else    Fail($"{url} → expected {(shouldBeSkipped ? "skipped" : "kept")} but got {(r is null ? "skipped" : "kept")}");
    }

    private void AssertCanonical(string url, string expected)
    {
        var r = DomainExtractor.Extract(url);
        if (r is null) { Fail($"{url} → unexpectedly skipped"); return; }
        if (string.Equals(r.CanonicalDomain, expected, StringComparison.OrdinalIgnoreCase))
            Pass($"{url} → {r.CanonicalDomain}");
        else
            Fail($"{url} → canonical {r.CanonicalDomain}, expected {expected}");
    }

    private void RunUrlClassifierTests()
    {
        Console.WriteLine("UrlClassifier tests:");
        // Job detail pages (numeric or slug ID in path)
        AssertKind("https://boards.greenhouse.io/acme/jobs/12345",                  Models.UrlKind.JobDetail);
        AssertKind("https://careers.example.com/jobs/senior-backend-engineer-456",  Models.UrlKind.JobDetail);
        AssertKind("https://acme.com/careers/openings/12345",                       Models.UrlKind.JobDetail);

        // Department/category filters
        AssertKind("https://careers.hootsuite.com/jobs?department=Engineering",     Models.UrlKind.Department);
        AssertKind("https://acme.com/careers/team/engineering",                     Models.UrlKind.Department);
        AssertKind("https://acme.com/jobs?category=design",                         Models.UrlKind.Department);

        // Flat job listing pages
        AssertKind("https://acme.com/careers",                                      Models.UrlKind.JobList);
        AssertKind("https://acme.com/jobs",                                         Models.UrlKind.JobList);

        // Non-job links should be rejected (null)
        AssertKindNull("https://acme.com/login");
        AssertKindNull("https://acme.com/privacy");
        AssertKindNull("https://acme.com/blog/some-post");
        AssertKindNull("https://twitter.com/acme");
        AssertKindNull("https://linkedin.com/company/acme");
        Console.WriteLine();
    }

    private void RunLocationMatcherTests()
    {
        Console.WriteLine("LocationMatcher tests:");
        // Vancouver-area cities — accept
        AssertLocationKept("Vancouver, BC, Canada");
        AssertLocationKept("Burnaby");
        AssertLocationKept("Remote, Canada");
        AssertLocationKept("Remote - Canada");
        AssertLocationKept("Remote (North America)");
        AssertLocationKept(null);   // unknown — pass through
        AssertLocationKept("");
        AssertLocationKept("Remote");                              // pure remote
        AssertLocationKept("Remote (United States | Canada)");     // explicit dual-region

        // US-only / UK-only / other-region — reject. These ALL slipped through the previous
        // matcher because they didn't list a city, just a country / region tag.
        AssertLocationRejected("Remote - United States");
        AssertLocationRejected("Remote U.S.");
        AssertLocationRejected("Remote US");
        AssertLocationRejected("United States - Remote");
        AssertLocationRejected("USA - Remote");
        AssertLocationRejected("Remote UK");
        AssertLocationRejected("Remote - United Kingdom");
        AssertLocationRejected("Remote, EMEA");
        AssertLocationRejected("New York, NY");
        AssertLocationRejected("Toronto, ON");
        Console.WriteLine();
    }

    private void AssertLocationKept(string? location)
    {
        var keep = Jobnet.Services.Location.LocationMatcher.IsVancouverArea(location);
        var label = location ?? "(null)";
        if (keep) Pass($"\"{label}\" → kept");
        else      Fail($"\"{label}\" → rejected (expected kept)");
    }

    private void AssertLocationRejected(string? location)
    {
        var keep = Jobnet.Services.Location.LocationMatcher.IsVancouverArea(location);
        var label = location ?? "(null)";
        if (!keep) Pass($"\"{label}\" → rejected");
        else       Fail($"\"{label}\" → kept (expected rejected)");
    }

    private void AssertKind(string url, string expectedKind)
    {
        var k = Jobnet.Services.JobSources.UrlClassifier.Classify(url);
        if (k == expectedKind) Pass($"{url} → {k}");
        else                   Fail($"{url} → {k ?? "(null)"} (expected {expectedKind})");
    }

    private void AssertKindNull(string url)
    {
        var k = Jobnet.Services.JobSources.UrlClassifier.Classify(url);
        if (k is null) Pass($"{url} → (skipped)");
        else           Fail($"{url} → {k} (expected null)");
    }

    private async Task RunRateLimiterTests(IServiceProvider services)
    {
        Console.WriteLine("RateLimiter tests:");
        var limiter = services.GetRequiredService<IRateLimiter>();
        var configRepo = services.GetRequiredService<Jobnet.Data.Repositories.IConfigRepository>();

        // Configure a 250ms delay for a fake provider, then verify two back-to-back waits respect it.
        const string fake = "test_fake_provider";
        configRepo.Set($"api_min_delay_ms.{fake}", "250");

        var t0 = DateTime.UtcNow;
        await limiter.WaitAsync(fake);
        await limiter.WaitAsync(fake);
        var elapsed = DateTime.UtcNow - t0;

        if (elapsed >= TimeSpan.FromMilliseconds(230))   // ~250ms ± tolerance
            Pass($"two calls with 250ms min-delay took {elapsed.TotalMilliseconds:F0}ms (≥230)");
        else
            Fail($"two calls with 250ms min-delay took only {elapsed.TotalMilliseconds:F0}ms");

        // Provider with no config → no delay
        configRepo.Set("api_min_delay_ms.test_no_delay", "0");
        t0 = DateTime.UtcNow;
        await limiter.WaitAsync("test_no_delay");
        await limiter.WaitAsync("test_no_delay");
        elapsed = DateTime.UtcNow - t0;
        if (elapsed < TimeSpan.FromMilliseconds(100))
            Pass($"two calls with 0ms delay completed in {elapsed.TotalMilliseconds:F0}ms (<100)");
        else
            Fail($"two calls with 0ms delay took {elapsed.TotalMilliseconds:F0}ms (expected fast)");
        Console.WriteLine();
    }

    private void RunMigrationTests(IServiceProvider services)
    {
        Console.WriteLine("Migration tests:");
        var connections = services.GetRequiredService<IDbConnectionFactory>();
        using var conn = connections.Open();

        var count = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM schema_migrations");
        if (count >= 6) Pass($"schema_migrations rows: {count} (≥6)");
        else            Fail($"schema_migrations rows: {count} (expected ≥6)");

        var hasLevels = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM levels") > 0;
        var hasAreas  = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM areas") > 0;
        if (hasLevels) Pass("levels table seeded"); else Fail("levels table empty");
        if (hasAreas)  Pass("areas table seeded");  else Fail("areas table empty");

        // Critical pragmas
        var journalMode = conn.ExecuteScalar<string>("PRAGMA journal_mode");
        if (journalMode == "wal") Pass("journal_mode=wal");
        else                       Fail($"journal_mode={journalMode} (expected wal)");

        var fk = conn.ExecuteScalar<int>("PRAGMA foreign_keys");
        if (fk == 1) Pass("foreign_keys=on");
        else         Fail($"foreign_keys={fk} (expected 1)");
        Console.WriteLine();
    }

    private void Pass(string msg) { _pass++; Console.WriteLine($"  ✓ {msg}"); }
    private void Fail(string msg) { _fail++; Console.WriteLine($"  ✗ {msg}"); }
}
