using System.Collections.Generic;
using Jobnet.Models;

namespace Jobnet.Data.Repositories;

public interface ISavedFilterRepository
{
    IReadOnlyList<SavedFilter> GetAll();
    SavedFilter? GetByName(string name);
    int Upsert(string name, string payloadJson);
    void Delete(int id);
    void MarkUsed(int id);

    /// <summary>Rename an existing saved filter. Throws if newName conflicts with another row.</summary>
    void Rename(int id, string newName);
}
