using System;
using System.Collections.Generic;
using System.Linq;
using Jobnet.Data.Repositories;
using Jobnet.Models;
using Jobnet.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class SeedFakeCommand : ICliCommand
{
    public string Name => "seed-fake";
    public string Description => "Populate the database with FakeJobDataService data (idempotent — skips existing).";

    public int Run(string[] args, IServiceProvider services)
    {
        var fake = services.GetRequiredService<FakeJobDataService>();
        var companies = services.GetRequiredService<ICompanyRepository>();
        var jobs = services.GetRequiredService<IJobRepository>();
        var levels = services.GetRequiredService<ILevelRepository>();
        var areas = services.GetRequiredService<IAreaRepository>();

        var levelByName = levels.GetAll().ToDictionary(l => l.Name, l => l.Id, StringComparer.OrdinalIgnoreCase);
        var areaByName  = areas.GetAll().ToDictionary(a => a.Name, a => a.Id, StringComparer.OrdinalIgnoreCase);

        var companyIdMap = new Dictionary<int, int>();
        var companiesAdded = 0;
        var companiesSkipped = 0;

        foreach (var fakeCompany in fake.GetCompanies())
        {
            var existing = companies.GetByDomain(fakeCompany.Domain);
            if (existing is not null)
            {
                companyIdMap[fakeCompany.Id] = existing.Id;
                companiesSkipped++;
                continue;
            }

            var toInsert = new Company
            {
                Id = 0,
                Name = fakeCompany.Name,
                Domain = fakeCompany.Domain,
                CareersUrl = fakeCompany.CareersUrl,
                City = fakeCompany.City,
                AtsType = fakeCompany.AtsType,
                InterestLevel = fakeCompany.InterestLevel,
                DateDiscovered = fakeCompany.DateDiscovered,
                DateLastScan = fakeCompany.DateLastScan,
            };
            var newId = companies.Insert(toInsert);
            if (fakeCompany.InterestLevel != InterestLevel.Neutral)
                companies.SetInterestLevel(newId, fakeCompany.InterestLevel);
            companyIdMap[fakeCompany.Id] = newId;
            companiesAdded++;
        }

        Console.WriteLine($"Companies: {companiesAdded} added, {companiesSkipped} skipped (existing).");

        var jobsAdded = 0;
        var jobsSkipped = 0;
        var unresolvedLevels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolvedAreas  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var seed in fake.GetJobs())
        {
            if (!companyIdMap.TryGetValue(seed.FakeCompanyId, out var realCompanyId)) continue;

            int? levelId = levelByName.TryGetValue(seed.LevelCategory, out var lid) ? lid : (int?)null;
            if (levelId is null) unresolvedLevels.Add(seed.LevelCategory);

            var areaIds = new List<int>();
            foreach (var a in seed.AreaCategories)
            {
                if (areaByName.TryGetValue(a, out var aid)) areaIds.Add(aid);
                else unresolvedAreas.Add(a);
            }

            var jobToInsert = new Job
            {
                Id = 0,
                CompanyId = realCompanyId,
                Title = seed.Title,
                Url = seed.Url,
                Location = seed.Location,
                RemoteType = seed.RemoteType,
                EmploymentType = seed.EmploymentType,
                LevelId = levelId,
                AreaIds = areaIds,
                DescriptionSnippet = seed.DescriptionSnippet,
                InterestLevel = seed.InterestLevel,
                DateFirstSeen = seed.DateFirstSeen,
                DateLastSeen = seed.DateLastSeen,
                DateRemoved = seed.DateRemoved,
                IsActive = seed.IsActive,
            };

            try
            {
                var newJobId = jobs.Insert(jobToInsert, hashTier: 3);
                if (seed.InterestLevel != InterestLevel.Neutral)
                    jobs.SetInterestLevel(newJobId, seed.InterestLevel);
                if (!seed.IsActive && seed.DateRemoved.HasValue)
                    jobs.MarkRemoved(newJobId, seed.DateRemoved.Value);
                jobsAdded++;
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                jobsSkipped++;
            }
        }

        Console.WriteLine($"Jobs: {jobsAdded} added, {jobsSkipped} skipped (already present).");
        if (unresolvedLevels.Count > 0)
            Console.WriteLine($"WARNING: unresolved level names: {string.Join(", ", unresolvedLevels)}");
        if (unresolvedAreas.Count > 0)
            Console.WriteLine($"WARNING: unresolved area names: {string.Join(", ", unresolvedAreas)}");
        return 0;
    }
}
