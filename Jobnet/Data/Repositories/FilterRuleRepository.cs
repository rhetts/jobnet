using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using Jobnet.Models;

namespace Jobnet.Data.Repositories;

public sealed class FilterRuleRepository : IFilterRuleRepository
{
    private readonly IDbConnectionFactory _connections;

    public FilterRuleRepository(IDbConnectionFactory connections)
    {
        _connections = connections;
    }

    private const string SelectAll = @"
        SELECT id, subject, action, match_type AS MatchType, pattern, scope, note,
               is_enabled AS IsEnabled, hit_count AS HitCount, last_hit AS LastHitIso
        FROM filter_rule";

    public IReadOnlyList<FilterRule> GetAll()
    {
        using var conn = _connections.Open();
        return conn.Query<Row>($"{SelectAll} ORDER BY subject, scope, pattern").Select(Map).ToList();
    }

    public IReadOnlyList<FilterRule> GetEnabled()
    {
        using var conn = _connections.Open();
        return conn.Query<Row>($"{SelectAll} WHERE is_enabled = 1 ORDER BY subject, scope, pattern")
                   .Select(Map).ToList();
    }

    public IReadOnlyList<FilterRule> GetBySubject(string subject)
    {
        using var conn = _connections.Open();
        return conn.Query<Row>($"{SelectAll} WHERE subject = @subject ORDER BY scope, pattern",
            new { subject }).Select(Map).ToList();
    }

    public int Insert(FilterRule rule)
    {
        using var conn = _connections.Open();
        return conn.ExecuteScalar<int>(@"
            INSERT INTO filter_rule (subject, action, match_type, pattern, scope, note, is_enabled, date_added)
            VALUES (@Subject, @Action, @MatchType, @Pattern, @Scope, @Note, @IsEnabled, @now);
            SELECT last_insert_rowid();",
            new
            {
                rule.Subject, rule.Action, rule.MatchType, rule.Pattern, rule.Scope, rule.Note,
                IsEnabled = rule.IsEnabled ? 1 : 0,
                now = DateTime.UtcNow.ToString("o"),
            });
    }

    public void Update(FilterRule rule)
    {
        using var conn = _connections.Open();
        conn.Execute(@"
            UPDATE filter_rule
               SET subject = @Subject, action = @Action, match_type = @MatchType,
                   pattern = @Pattern, scope = @Scope, note = @Note, is_enabled = @IsEnabled
             WHERE id = @Id",
            new
            {
                rule.Id, rule.Subject, rule.Action, rule.MatchType, rule.Pattern, rule.Scope, rule.Note,
                IsEnabled = rule.IsEnabled ? 1 : 0,
            });
    }

    public void Delete(int id)
    {
        using var conn = _connections.Open();
        conn.Execute("DELETE FROM filter_rule WHERE id = @id", new { id });
    }

    public void SetEnabled(int id, bool enabled)
    {
        using var conn = _connections.Open();
        conn.Execute("UPDATE filter_rule SET is_enabled = @enabled WHERE id = @id",
            new { id, enabled = enabled ? 1 : 0 });
    }

    public void RecordHits(IReadOnlyDictionary<int, int> hitsByRuleId)
    {
        if (hitsByRuleId.Count == 0) return;
        var now = DateTime.UtcNow.ToString("o");
        using var conn = _connections.Open();
        using var tx = conn.BeginTransaction();
        foreach (var (id, count) in hitsByRuleId)
        {
            if (count <= 0) continue;
            conn.Execute(
                "UPDATE filter_rule SET hit_count = hit_count + @count, last_hit = @now WHERE id = @id",
                new { id, count, now }, tx);
        }
        tx.Commit();
    }

    private static FilterRule Map(Row r) => new()
    {
        Id = r.Id,
        Subject = r.Subject,
        Action = r.Action,
        MatchType = r.MatchType,
        Pattern = r.Pattern,
        Scope = string.IsNullOrEmpty(r.Scope) ? null : r.Scope,
        Note = r.Note,
        IsEnabled = r.IsEnabled != 0,
        HitCount = r.HitCount,
        LastHit = string.IsNullOrEmpty(r.LastHitIso) ? null : DateTime.Parse(r.LastHitIso).ToUniversalTime(),
    };

    private sealed class Row
    {
        public int Id { get; set; }
        public string Subject { get; set; } = "";
        public string Action { get; set; } = "";
        public string MatchType { get; set; } = "";
        public string Pattern { get; set; } = "";
        public string? Scope { get; set; }
        public string? Note { get; set; }
        public int IsEnabled { get; set; }
        public int HitCount { get; set; }
        public string? LastHitIso { get; set; }
    }
}
