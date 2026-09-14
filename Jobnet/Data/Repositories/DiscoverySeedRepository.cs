using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using Jobnet.Models;

namespace Jobnet.Data.Repositories;

public sealed class DiscoverySeedRepository : IDiscoverySeedRepository
{
    private const string SelectAll = @"
        SELECT id, name, url, description,
               is_enabled AS IsEnabled,
               sort_order AS SortOrder,
               max_pages  AS MaxPages,
               date_added AS DateAdded,
               custom_parser_name AS CustomParserName,
               custom_parser_last_result AS CustomParserLastResult,
               custom_parser_last_error AS CustomParserLastError,
               custom_parser_last_result_at AS CustomParserLastResultAt
        FROM discovery_seeds";

    private readonly IDbConnectionFactory _connections;

    public DiscoverySeedRepository(IDbConnectionFactory connections)
    {
        _connections = connections;
    }

    public IReadOnlyList<DiscoverySeed> GetAll()
    {
        using var conn = _connections.Open();
        return conn.Query<SeedRow>($"{SelectAll} ORDER BY sort_order, name")
            .Select(Map).ToList();
    }

    public IReadOnlyList<DiscoverySeed> GetEnabled()
    {
        using var conn = _connections.Open();
        return conn.Query<SeedRow>($"{SelectAll} WHERE is_enabled = 1 ORDER BY sort_order, name")
            .Select(Map).ToList();
    }

    public int Insert(string name, string url, string? description, bool isEnabled, int sortOrder, int maxPages = 1)
    {
        using var conn = _connections.Open();
        return (int)conn.ExecuteScalar<long>(@"
            INSERT INTO discovery_seeds (name, url, description, is_enabled, sort_order, max_pages)
            VALUES (@name, @url, @description, @isEnabled, @sortOrder, @maxPages);
            SELECT last_insert_rowid();",
            new { name, url, description, isEnabled = isEnabled ? 1 : 0, sortOrder, maxPages });
    }

    public void Update(int id, string name, string url, string? description, bool isEnabled, int sortOrder, int maxPages = 1)
    {
        using var conn = _connections.Open();
        conn.Execute(@"
            UPDATE discovery_seeds SET
                name = @name,
                url = @url,
                description = @description,
                is_enabled = @isEnabled,
                sort_order = @sortOrder,
                max_pages = @maxPages
            WHERE id = @id",
            new { id, name, url, description, isEnabled = isEnabled ? 1 : 0, sortOrder, maxPages });
    }

    public void Delete(int id)
    {
        using var conn = _connections.Open();
        conn.Execute("DELETE FROM discovery_seeds WHERE id = @id", new { id });
    }

    public void SetEnabled(int id, bool isEnabled)
    {
        using var conn = _connections.Open();
        conn.Execute("UPDATE discovery_seeds SET is_enabled = @v WHERE id = @id",
            new { id, v = isEnabled ? 1 : 0 });
    }

    public void SetParserResult(int id, string parserName, string result, string? error, DateTime whenUtc)
    {
        using var conn = _connections.Open();
        conn.Execute(@"
            UPDATE discovery_seeds SET
                custom_parser_name = @parserName,
                custom_parser_last_result = @result,
                custom_parser_last_error = @error,
                custom_parser_last_result_at = @when
            WHERE id = @id",
            new { id, parserName, result, error, when = whenUtc.ToString("o") });
    }

    private static DiscoverySeed Map(SeedRow r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        Url = r.Url,
        Description = r.Description,
        IsEnabled = r.IsEnabled != 0,
        SortOrder = r.SortOrder,
        MaxPages = r.MaxPages <= 0 ? 1 : r.MaxPages,
        DateAdded = string.IsNullOrEmpty(r.DateAdded) ? DateTime.MinValue : DateTime.Parse(r.DateAdded).ToUniversalTime(),
        CustomParserName = r.CustomParserName,
        CustomParserLastResult = r.CustomParserLastResult,
        CustomParserLastError = r.CustomParserLastError,
        CustomParserLastResultAt = string.IsNullOrEmpty(r.CustomParserLastResultAt)
            ? null : DateTime.Parse(r.CustomParserLastResultAt).ToUniversalTime(),
    };

    private sealed class SeedRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string? Description { get; set; }
        public int IsEnabled { get; set; }
        public int SortOrder { get; set; }
        public int MaxPages { get; set; } = 1;
        public string? DateAdded { get; set; }
        public string? CustomParserName { get; set; }
        public string? CustomParserLastResult { get; set; }
        public string? CustomParserLastError { get; set; }
        public string? CustomParserLastResultAt { get; set; }
    }
}
