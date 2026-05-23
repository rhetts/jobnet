using System.Collections.Generic;
using System.Linq;
using Jobnet.Data;
using Jobnet.Data.Repositories;

namespace Jobnet.Services.Classification;

public interface IJobReclassifier
{
    JobReclassifyReport ReclassifyAll();
}

public sealed class JobReclassifyReport
{
    public int Examined { get; set; }
    public int Changed  { get; set; }
}

/// <summary>Re-runs the heuristic classifier across every active job. Useful after the
/// taxonomy changes (new areas, new rules) so old jobs stop being mis-tagged as "Other".</summary>
public sealed class JobReclassifier : IJobReclassifier
{
    private readonly IJobRepository _jobs;
    private readonly IAreaRepository _areas;
    private readonly IDbConnectionFactory _connections;
    private readonly HeuristicClassifier _heuristic;
    private readonly ICompanyRepository _companies;

    public JobReclassifier(IJobRepository jobs, IAreaRepository areas,
                            IDbConnectionFactory connections,
                            HeuristicClassifier heuristic,
                            ICompanyRepository companies)
    {
        _jobs = jobs;
        _areas = areas;
        _connections = connections;
        _heuristic = heuristic;
        _companies = companies;
    }

    public JobReclassifyReport ReclassifyAll()
    {
        var report = new JobReclassifyReport();
        var all = _jobs.GetAll(includeRemoved: false);
        foreach (var job in all)
        {
            report.Examined++;
            // The classifier may use department text via the title hay; for refresh-time
            // we only have title (department isn't stored on jobs). That's fine — title
            // alone gives a good signal for most roles.
            var result = _heuristic.Classify(job.Title, department: null);

            var newLevelId = result.LevelId;
            var newAreaIds = result.Areas.Select(a => a.Id).Distinct().ToList();

            // Compare with existing.
            var existingAreaIds = new HashSet<int>(job.AreaIds);
            var newAreaSet = new HashSet<int>(newAreaIds);
            var areasChanged = !existingAreaIds.SetEquals(newAreaSet);
            var levelChanged = newLevelId != job.LevelId;

            if (!levelChanged && !areasChanged) continue;

            if (levelChanged) _jobs.SetLevel(job.Id, newLevelId);
            if (areasChanged) _areas.SetAreasForJob(job.Id, newAreaIds);
            report.Changed++;
        }
        return report;
    }
}
