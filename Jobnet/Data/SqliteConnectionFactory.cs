using System.Data;
using Dapper;
using Jobnet.Services;
using Microsoft.Data.Sqlite;

namespace Jobnet.Data;

public sealed class SqliteConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(IAppPaths paths)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true
        };
        _connectionString = builder.ToString();
    }

    public IDbConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        // Pragmas applied per-connection (see REQUIREMENTS.md Decision 16)
        conn.Execute("PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;");
        return conn;
    }
}
