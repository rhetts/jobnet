using System.Collections.Generic;
using Jobnet.Models;

namespace Jobnet.Data.Repositories;

public interface IDiscoverySeedRepository
{
    IReadOnlyList<DiscoverySeed> GetAll();
    IReadOnlyList<DiscoverySeed> GetEnabled();
    int Insert(string name, string url, string? description, bool isEnabled, int sortOrder, int maxPages = 1);
    void Update(int id, string name, string url, string? description, bool isEnabled, int sortOrder, int maxPages = 1);
    void Delete(int id);
    void SetEnabled(int id, bool isEnabled);
}
