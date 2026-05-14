using System.Collections.Generic;
using Jobnet.Models;

namespace Jobnet.Data.Repositories;

public interface IAreaRepository
{
    IReadOnlyList<Area> GetAll();
    Area? GetByName(string name);
    int Insert(string name, int sortOrder);
    void Update(Area area);
    void Delete(int id);
    void Reorder(IReadOnlyList<int> orderedIds);

    IReadOnlyList<int> GetAreaIdsForJob(int jobId);
    void SetAreasForJob(int jobId, IReadOnlyList<int> areaIds);
}
