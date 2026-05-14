using System.Collections.Generic;

namespace Jobnet.Data.Repositories;

public interface IConfigRepository
{
    string? Get(string key);
    string GetOrDefault(string key, string fallback);
    IReadOnlyDictionary<string, string> GetAll();
    void Set(string key, string value);
}
