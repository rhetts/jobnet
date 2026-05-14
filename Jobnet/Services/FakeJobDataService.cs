using System;
using System.Collections.Generic;
using Jobnet.Models;

namespace Jobnet.Services;

public sealed class FakeJobSeed
{
    public required int FakeId { get; init; }
    public required int FakeCompanyId { get; init; }
    public required string Title { get; init; }
    public string? Url { get; init; }
    public string? Location { get; init; }
    public string? RemoteType { get; init; }
    public string? EmploymentType { get; init; }
    public string LevelCategory { get; init; } = "Unknown";
    public IReadOnlyList<string> AreaCategories { get; init; } = Array.Empty<string>();
    public string? DescriptionSnippet { get; init; }
    public InterestLevel InterestLevel { get; init; }
    public required DateTime DateFirstSeen { get; init; }
    public required DateTime DateLastSeen { get; init; }
    public DateTime? DateRemoved { get; init; }
    public bool IsActive { get; init; } = true;
}

public sealed class FakeJobDataService
{
    private readonly List<Company> _companies;
    private readonly List<FakeJobSeed> _jobs;

    public FakeJobDataService()
    {
        var now = DateTime.UtcNow;

        _companies = new()
        {
            new Company { Id = 1,  Name = "Acme Corp",        Domain = "acme.com",        City = "Vancouver", AtsType = "greenhouse", InterestLevel = InterestLevel.Interesting,    DateDiscovered = now.AddDays(-90),  DateLastScan = now.AddHours(-3) },
            new Company { Id = 2,  Name = "Hootsuite",        Domain = "hootsuite.com",   City = "Vancouver", AtsType = "greenhouse", InterestLevel = InterestLevel.Neutral,        DateDiscovered = now.AddDays(-120), DateLastScan = now.AddHours(-5) },
            new Company { Id = 3,  Name = "Slack",            Domain = "slack.com",       City = "Vancouver", AtsType = "lever",      InterestLevel = InterestLevel.NotInteresting, DateDiscovered = now.AddDays(-200), DateLastScan = now.AddHours(-12) },
            new Company { Id = 4,  Name = "Coinbase",         Domain = "coinbase.com",    City = "Vancouver", AtsType = "greenhouse", InterestLevel = InterestLevel.Neutral,        DateDiscovered = now.AddDays(-60),  DateLastScan = now.AddHours(-2) },
            new Company { Id = 5,  Name = "Shopify",          Domain = "shopify.com",     City = "Vancouver", AtsType = "custom",     InterestLevel = InterestLevel.Interesting,    DateDiscovered = now.AddDays(-150), DateLastScan = now.AddHours(-1) },
            new Company { Id = 6,  Name = "Klue",             Domain = "klue.com",        City = "Vancouver", AtsType = "ashby",      InterestLevel = InterestLevel.Neutral,        DateDiscovered = now.AddDays(-30),  DateLastScan = now.AddHours(-4) },
            new Company { Id = 7,  Name = "Trulioo",          Domain = "trulioo.com",     City = "Vancouver", AtsType = "greenhouse", InterestLevel = InterestLevel.Neutral,        DateDiscovered = now.AddDays(-45),  DateLastScan = now.AddHours(-6) },
            new Company { Id = 8,  Name = "TELUS",            Domain = "telus.com",       City = "Burnaby",   AtsType = "workday",    InterestLevel = InterestLevel.Neutral,        DateDiscovered = now.AddDays(-180), DateLastScan = now.AddHours(-8) },
            new Company { Id = 9,  Name = "Bench Accounting", Domain = "bench.co",        City = "Vancouver", AtsType = "lever",      InterestLevel = InterestLevel.Interesting,    DateDiscovered = now.AddDays(-72),  DateLastScan = now.AddHours(-2) },
            new Company { Id = 10, Name = "Aurinko",          Domain = "aurinko.io",      City = "Vancouver", AtsType = "ashby",      InterestLevel = InterestLevel.Neutral,        DateDiscovered = now.AddDays(-15),  DateLastScan = now.AddHours(-1) },
            new Company { Id = 11, Name = "Visier",           Domain = "visier.com",      City = "Vancouver", AtsType = "greenhouse", InterestLevel = InterestLevel.Neutral,        DateDiscovered = now.AddDays(-100), DateLastScan = now.AddHours(-7) },
            new Company { Id = 12, Name = "Mobify",           Domain = "mobify.com",      City = "Vancouver", AtsType = "lever",      InterestLevel = InterestLevel.Neutral,        DateDiscovered = now.AddDays(-220), DateLastScan = now.AddHours(-10) },
        };

        _jobs = new()
        {
            J(101, 1,  "Senior Backend Engineer",         "remote",  "full-time", "Senior",            "Software Engineering", now.AddDays(-3)),
            J(102, 1,  "Staff Engineer, Platform",        "hybrid",  "full-time", "Staff / Principal", "Software Engineering", now.AddDays(-7)),
            J(103, 1,  "Junior Data Analyst",             "on-site", "full-time", "Junior",            "Data / ML",            now.AddDays(-1)),
            J(104, 1,  "Engineering Manager",             "hybrid",  "full-time", "Manager",           "Management",           now.AddDays(-21)),
            J(105, 1,  "Marketing Coordinator",           "on-site", "full-time", "Junior",            "Other",                now.AddDays(-60), interest: InterestLevel.NotInteresting),

            J(201, 2,  "Senior Software Engineer, API",   "remote",  "full-time", "Senior",            "Software Engineering", now.AddDays(-14)),
            J(202, 2,  "Frontend Developer",              "hybrid",  "full-time", "Mid",               "Software Engineering", now.AddDays(-9)),
            J(203, 2,  "DevOps Engineer",                 "remote",  "full-time", "Senior",            "DevOps / Platform",    now.AddDays(-5)),
            J(204, 2,  "Product Designer",                "hybrid",  "full-time", "Mid",               "Design",               now.AddDays(-30)),

            J(301, 3,  "Sales Engineer",                  "remote",  "full-time", "Senior",            "Other",                now.AddDays(-45)),
            J(302, 3,  "Customer Success Manager",        "remote",  "full-time", "Manager",           "Management",           now.AddDays(-90), interest: InterestLevel.NotInteresting),

            J(401, 4,  "Senior Blockchain Engineer",      "remote",  "full-time", "Senior",            "Software Engineering", now.AddDays(-12)),
            J(402, 4,  "Security Engineer",               "hybrid",  "full-time", "Senior",            "Security",             now.AddDays(-22)),
            J(403, 4,  "Staff iOS Engineer",              "remote",  "full-time", "Staff / Principal", "Software Engineering", now.AddDays(-4),  interest: InterestLevel.Interesting),

            J(501, 5,  "Senior Rails Engineer",           "remote",  "full-time", "Senior",            "Software Engineering", now.AddDays(-2),  interest: InterestLevel.Interesting),
            J(502, 5,  "Staff Engineer, Storefront",      "hybrid",  "full-time", "Staff / Principal", "Software Engineering", now.AddDays(-6)),
            J(503, 5,  "Director of Engineering",         "hybrid",  "full-time", "Director",          "Management",           now.AddDays(-18)),
            J(504, 5,  "ML Engineer, Search",             "remote",  "full-time", "Senior",            "Data / ML",            now.AddDays(-11)),
            J(505, 5,  "Engineering Intern (Summer)",     "on-site", "contract",  "Junior",            "Software Engineering", now.AddDays(-25)),

            J(601, 6,  "Full Stack Developer",            "hybrid",  "full-time", "Mid",               "Software Engineering", now.AddDays(-8)),
            J(602, 6,  "Senior Data Engineer",            "remote",  "full-time", "Senior",            "Data / ML",            now.AddDays(-19)),

            J(701, 7,  "Backend Engineer, Identity",      "remote",  "full-time", "Senior",            "Software Engineering", now.AddDays(-13)),
            J(702, 7,  "QA Automation Engineer",          "hybrid",  "full-time", "Mid",               "QA / Test",            now.AddDays(-27)),

            J(801, 8,  "Network Engineer",                "on-site", "full-time", "Senior",            "DevOps / Platform",    now.AddDays(-40)),
            J(802, 8,  "Software Developer III",          "hybrid",  "full-time", "Senior",            "Software Engineering", now.AddDays(-55)),
            J(803, 8,  "Project Manager",                 "on-site", "full-time", "Manager",           "Management",           now.AddDays(-70), interest: InterestLevel.NotInteresting),
            J(804, 8,  "Retail Store Manager",            "on-site", "full-time", "Manager",           "Other",                now.AddDays(-85), interest: InterestLevel.NotInteresting),

            J(901, 9,  "Senior Software Engineer, Tax",   "remote",  "full-time", "Senior",            "Software Engineering", now.AddDays(-3),  interest: InterestLevel.Interesting),
            J(902, 9,  "Staff Engineer, Payments",        "remote",  "full-time", "Staff / Principal", "Software Engineering", now.AddDays(-10)),
            J(903, 9,  "Bookkeeper",                      "on-site", "full-time", "Junior",            "Other",                now.AddDays(-50), interest: InterestLevel.NotInteresting),

            J(1001, 10, "Founding Engineer",               "hybrid",  "full-time", "Staff / Principal", "Software Engineering", now.AddDays(-1)),
            J(1002, 10, "Lead Engineer (Backend)",         "remote",  "full-time", "Lead",              "Software Engineering", now.AddDays(-6)),

            J(1101, 11, "Senior Engineer, Analytics",      "remote",  "full-time", "Senior",            "Data / ML",            now.AddDays(-16)),
            J(1102, 11, "Director, Engineering",           "hybrid",  "full-time", "Director",          "Management",           now.AddDays(-35)),

            J(1201, 12, "Software Engineer",               "remote",  "full-time", "Mid",               "Software Engineering", now.AddDays(-110), active: false, removedDaysAgo: 30),
            J(1202, 12, "Lead Developer",                  "hybrid",  "full-time", "Lead",              "Software Engineering", now.AddDays(-200), active: false, removedDaysAgo: 60),
        };
    }

    public IReadOnlyList<Company> GetCompanies() => _companies;
    public IReadOnlyList<FakeJobSeed> GetJobs() => _jobs;

    private static FakeJobSeed J(int id, int companyId, string title, string remote, string emp,
        string level, string area, DateTime firstSeen,
        bool active = true, InterestLevel interest = InterestLevel.Neutral, int? removedDaysAgo = null)
    {
        var now = DateTime.UtcNow;
        return new FakeJobSeed
        {
            FakeId = id,
            FakeCompanyId = companyId,
            Title = title,
            Url = $"https://example.com/jobs/{id}",
            Location = "Vancouver, BC",
            RemoteType = remote,
            EmploymentType = emp,
            LevelCategory = level,
            AreaCategories = new[] { area },
            DateFirstSeen = firstSeen,
            DateLastSeen = active ? now : now.AddDays(-removedDaysAgo ?? 0),
            DateRemoved = active ? null : now.AddDays(-(removedDaysAgo ?? 0)),
            IsActive = active,
            InterestLevel = interest,
            DescriptionSnippet = $"Sample description for {title}. Tech stack varies. Vancouver-based."
        };
    }
}
