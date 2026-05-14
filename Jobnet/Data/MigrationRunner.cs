using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Jobnet.Data;

public sealed class MigrationRunner
{
    private const string SchemaTable = "schema_migrations";
    private const string ResourcePrefix = "Jobnet.Data.Migrations.";

    private readonly IDbConnectionFactory _connections;
    private readonly ILogger<MigrationRunner> _logger;

    public MigrationRunner(IDbConnectionFactory connections, ILogger<MigrationRunner> logger)
    {
        _connections = connections;
        _logger = logger;
    }

    public void Run()
    {
        using var conn = _connections.Open();

        conn.Execute($@"
            CREATE TABLE IF NOT EXISTS {SchemaTable} (
                name        TEXT PRIMARY KEY,
                applied_at  TEXT NOT NULL
            );");

        var applied = conn.Query<string>($"SELECT name FROM {SchemaTable}").ToHashSet();
        var pending = DiscoverMigrations().Where(m => !applied.Contains(m.Name)).ToList();

        if (pending.Count == 0)
        {
            _logger.LogInformation("Database up to date ({Count} migrations already applied).", applied.Count);
            return;
        }

        foreach (var migration in pending)
        {
            _logger.LogInformation("Applying migration {Name}...", migration.Name);
            using var tx = conn.BeginTransaction();
            conn.Execute(migration.Sql, transaction: tx);
            conn.Execute(
                $"INSERT INTO {SchemaTable} (name, applied_at) VALUES (@Name, @AppliedAt)",
                new { migration.Name, AppliedAt = DateTime.UtcNow.ToString("o") },
                tx);
            tx.Commit();
        }

        _logger.LogInformation("Applied {Count} new migration(s).", pending.Count);
    }

    private static IEnumerable<Migration> DiscoverMigrations()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal);

        foreach (var resource in names)
        {
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded resource not found: {resource}");
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();
            var name = resource.Substring(ResourcePrefix.Length);
            yield return new Migration(name, sql);
        }
    }

    private sealed record Migration(string Name, string Sql);
}
