using System.Collections.Generic;
using Jobnet.Models;

namespace Jobnet.Data.Repositories;

public interface IFilterRuleRepository
{
    /// <summary>Every rule, enabled or not. The UI needs the disabled ones too.</summary>
    IReadOnlyList<FilterRule> GetAll();

    /// <summary>Enabled rules only — what the matcher loads.</summary>
    IReadOnlyList<FilterRule> GetEnabled();

    IReadOnlyList<FilterRule> GetBySubject(string subject);

    int Insert(FilterRule rule);
    void Update(FilterRule rule);
    void Delete(int id);
    void SetEnabled(int id, bool enabled);

    /// <summary>Add the accumulated match counts. Called once at the end of a run rather than
    /// per match — a write per blocked URL would cost more than the filtering saves.</summary>
    void RecordHits(IReadOnlyDictionary<int, int> hitsByRuleId);
}
