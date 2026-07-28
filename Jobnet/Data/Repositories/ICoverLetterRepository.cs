using System;

namespace Jobnet.Data.Repositories;

public interface ICoverLetterRepository
{
    /// <summary>Latest stored letter for a job, or null if the user has never generated one.</summary>
    StoredCoverLetter? Get(int jobId);

    /// <summary>Save (insert or replace) a freshly generated letter — also updates updated_at.</summary>
    void SaveGenerated(int jobId, string text, string? model, DateTime generatedAt);

    /// <summary>Update letter_text + updated_at only. No-op if no row exists yet for the job
    /// (we never want manual edits to materialise a row before Generate has run).</summary>
    void UpdateText(int jobId, string text);
}

public sealed record StoredCoverLetter(string Text, string? Model, DateTime? GeneratedAt, DateTime UpdatedAt);
