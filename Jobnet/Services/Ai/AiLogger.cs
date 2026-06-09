using System;
using System.IO;
using System.Linq;

namespace Jobnet.Services.Ai;

/// <summary>
/// Dedicated file logger for AI-response parse failures. Writes to
/// <c>%LOCALAPPDATA%\Jobnet\ai-parse-errors.log</c> with timestamp, task tag, exception, the
/// raw provider response, AND (when supplied) the post-extraction text the JSON parser actually
/// choked on. Separate from <c>jobnet.log</c> so the AI failure record stays inspectable without
/// being drowned in WPF binding warnings.
///
/// Every write is wrapped in a try/catch — logging must never break the AI path. We're already
/// in an error-recovery branch when this is called; an IO failure here would only compound the
/// problem.
/// </summary>
public static class AiLogger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Jobnet", "ai-parse-errors.log");

    /// <summary>Append one block describing a JSON parse failure. <paramref name="rawResponse"/>
    /// is the original provider output; <paramref name="extractedJson"/> is the
    /// JsonExtractor.ExtractJsonObject result that JsonDocument.Parse refused (pass null if you
    /// don't have it separately).</summary>
    public static void LogParseFailure(
        string taskTag,
        Exception exception,
        string rawResponse,
        string? extractedJson = null,
        string? extraContext = null)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var sb = new System.Text.StringBuilder();
            sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("] task=").Append(taskTag).AppendLine();
            sb.Append("  exception: ").Append(exception.GetType().Name).Append(": ").AppendLine(exception.Message);
            if (!string.IsNullOrEmpty(extraContext))
                sb.Append("  context:   ").AppendLine(extraContext);

            sb.Append("  raw (").Append(rawResponse?.Length ?? 0).AppendLine(" chars):");
            sb.AppendLine(IndentAndTruncate(rawResponse ?? "", 2000));

            if (extractedJson is not null && !ReferenceEquals(extractedJson, rawResponse))
            {
                sb.Append("  extracted (").Append(extractedJson.Length).AppendLine(" chars):");
                sb.AppendLine(IndentAndTruncate(extractedJson, 2000));
            }

            sb.AppendLine(new string('-', 60));
            File.AppendAllText(LogPath, sb.ToString());
        }
        catch
        {
            // Logging must not break the calling AI path.
        }
    }

    private static string IndentAndTruncate(string s, int max)
    {
        var truncated = s.Length <= max
            ? s
            : s.Substring(0, max) + $"... [+{s.Length - max} chars truncated]";
        return string.Join("\n", truncated.Split('\n').Select(line => "    " + line));
    }
}
