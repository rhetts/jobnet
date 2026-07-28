namespace Jobnet.Services.CoverLetter;

public interface ICoverLetterPdfWriter
{
    /// <summary>Writes a cover letter to <paramref name="path"/> as PDF. Overwrites if it exists.
    /// The header line uses jobTitle + companyName; the body is rendered as plain prose
    /// (one paragraph per blank-line-separated block).</summary>
    void Write(string path, string companyName, string jobTitle, string letterText);
}
