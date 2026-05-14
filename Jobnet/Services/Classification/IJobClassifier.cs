namespace Jobnet.Services.Classification;

public interface IJobClassifier
{
    /// <summary>Classify a job by title (and optional department / hints).
    /// Maps to existing levels and areas in the DB — never invents new ones.</summary>
    ClassificationResult Classify(string title, string? department = null);
}
