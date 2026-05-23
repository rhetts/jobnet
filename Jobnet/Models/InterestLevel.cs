namespace Jobnet.Models;

public enum InterestLevel
{
    Neutral,
    /// <summary>User explicitly approved this job — moves into the "Approved jobs" tab and the
    /// applicant tracking flow. Was previously called "Interesting" (semantically equivalent).
    /// Migration 037 rewrites legacy "Interesting" values in the DB.</summary>
    Approved,
    NotInteresting
}
