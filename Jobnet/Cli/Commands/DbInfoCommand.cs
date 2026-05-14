using System;
using System.Linq;
using Dapper;
using Jobnet.Data;
using Jobnet.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class DbInfoCommand : ICliCommand
{
    public string Name => "db-info";
    public string Description => "Print DB path, table list with row counts, and applied migrations";

    public int Run(string[] args, IServiceProvider services)
    {
        var paths = services.GetRequiredService<IAppPaths>();
        var connections = services.GetRequiredService<IDbConnectionFactory>();

        Console.WriteLine($"Database: {paths.DatabasePath}");
        Console.WriteLine();

        using var conn = connections.Open();

        var tables = conn.Query<string>(
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name").ToList();

        Console.WriteLine($"Tables ({tables.Count}):");
        foreach (var t in tables)
        {
            var count = conn.ExecuteScalar<long>($"SELECT COUNT(*) FROM \"{t}\"");
            Console.WriteLine($"  {t,-22} {count,8} rows");
        }
        Console.WriteLine();

        var migrations = conn.Query<(string Name, string AppliedAt)>(
            "SELECT name, applied_at FROM schema_migrations ORDER BY name").ToList();
        Console.WriteLine($"Applied migrations ({migrations.Count}):");
        foreach (var m in migrations)
            Console.WriteLine($"  {m.Name,-30} at {m.AppliedAt}");

        return 0;
    }
}
