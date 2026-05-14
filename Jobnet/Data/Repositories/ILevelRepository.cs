using System.Collections.Generic;
using Jobnet.Models;

namespace Jobnet.Data.Repositories;

public interface ILevelRepository
{
    IReadOnlyList<Level> GetAll();
    Level? GetByName(string name);
    int Insert(string name, int sortOrder);
    void Update(Level level);
    void Delete(int id);
    void Reorder(IReadOnlyList<int> orderedIds);
}
