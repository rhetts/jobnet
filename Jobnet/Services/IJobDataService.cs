using System.Collections.Generic;
using Jobnet.Models;

namespace Jobnet.Services;

public interface IJobDataService
{
    IReadOnlyList<Company> GetCompanies();
    IReadOnlyList<Job> GetJobs();
    int ScoreJob(Job job);
}
