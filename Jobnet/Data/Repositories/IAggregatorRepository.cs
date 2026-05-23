using System.Collections.Generic;
using Jobnet.Models;

namespace Jobnet.Data.Repositories;

public interface IAggregatorRepository
{
    IReadOnlyList<AggregatorSource> GetAll();
    void SetEnabled(int id, bool enabled);
    int Insert(string name, string baseUrl, string? notes, bool isEnabled, int maxPages);
    void Update(int id, string name, string baseUrl, string? notes, bool isEnabled, int maxPages);
    void Delete(int id);
}
