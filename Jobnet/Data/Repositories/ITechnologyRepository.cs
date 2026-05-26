using System.Collections.Generic;
using Jobnet.Models;

namespace Jobnet.Data.Repositories;

public interface ITechnologyRepository
{
    /// <summary>All technologies, ordered by kind then name.</summary>
    IReadOnlyList<Technology> GetAll();

    /// <summary>Alias text -> technology ID. Built from technology_aliases. Used by the matcher
    /// to compile its regex set once at service start.</summary>
    IReadOnlyDictionary<string, int> GetAliasMap();

    /// <summary>Technology IDs currently tagged on a given job.</summary>
    IReadOnlyList<int> GetForJob(int jobId);

    /// <summary>Map of every job_id -> tagged technology IDs. Used by MainWindowViewModel to
    /// avoid N+1 when rendering the job list. Empty entries are omitted.</summary>
    Dictionary<int, List<int>> GetAllJobTechnologies();

    /// <summary>Replace the set of technologies tagged on a job (idempotent).</summary>
    void SetForJob(int jobId, IReadOnlyCollection<int> technologyIds);

    /// <summary>Count of active jobs per technology — drives the filter UI.</summary>
    Dictionary<int, int> GetActiveCountsByTechnology();
}
