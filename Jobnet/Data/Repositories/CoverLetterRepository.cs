using System;
using Dapper;

namespace Jobnet.Data.Repositories;

public sealed class CoverLetterRepository : ICoverLetterRepository
{
    private readonly IDbConnectionFactory _connections;

    public CoverLetterRepository(IDbConnectionFactory connections)
    {
        _connections = connections;
    }

    public StoredCoverLetter? Get(int jobId)
    {
        using var conn = _connections.Open();
        var row = conn.QuerySingleOrDefault<Row>(@"
            SELECT letter_text AS LetterText,
                   model        AS Model,
                   generated_at AS GeneratedAt,
                   updated_at   AS UpdatedAt
            FROM cover_letters WHERE job_id = @jobId", new { jobId });
        if (row is null) return null;
        return new StoredCoverLetter(
            row.LetterText,
            row.Model,
            string.IsNullOrEmpty(row.GeneratedAt) ? null : DateTime.Parse(row.GeneratedAt).ToUniversalTime(),
            DateTime.Parse(row.UpdatedAt).ToUniversalTime());
    }

    public void SaveGenerated(int jobId, string text, string? model, DateTime generatedAt)
    {
        var nowIso = DateTime.UtcNow.ToString("o");
        var genIso = generatedAt.ToUniversalTime().ToString("o");
        using var conn = _connections.Open();
        conn.Execute(@"
            INSERT INTO cover_letters (job_id, letter_text, model, generated_at, updated_at)
            VALUES (@jobId, @text, @model, @genIso, @nowIso)
            ON CONFLICT(job_id) DO UPDATE SET
                letter_text  = excluded.letter_text,
                model        = excluded.model,
                generated_at = excluded.generated_at,
                updated_at   = excluded.updated_at",
            new { jobId, text, model, genIso, nowIso });
    }

    public void UpdateText(int jobId, string text)
    {
        var nowIso = DateTime.UtcNow.ToString("o");
        using var conn = _connections.Open();
        conn.Execute(@"
            UPDATE cover_letters
            SET letter_text = @text, updated_at = @nowIso
            WHERE job_id = @jobId",
            new { jobId, text, nowIso });
    }

    private sealed class Row
    {
        public string LetterText { get; set; } = string.Empty;
        public string? Model { get; set; }
        public string? GeneratedAt { get; set; }
        public string UpdatedAt { get; set; } = string.Empty;
    }
}
