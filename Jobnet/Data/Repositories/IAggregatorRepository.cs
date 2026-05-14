using System.Collections.Generic;
using Jobnet.Models;

namespace Jobnet.Data.Repositories;

public interface IAggregatorRepository
{
    IReadOnlyList<AggregatorSource> GetAll();
    void SetEnabled(int id, bool enabled);
}
