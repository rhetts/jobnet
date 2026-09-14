using System;
using System.IO;

namespace Jobnet.Services.Discovery.DirectoryPatternParsers;

/// <summary>
/// Dedicated file logger for hand-written directory parser failures. Writes to
/// <c>%LOCALAPPDATA%\Jobnet\directory-parser-errors.log</c> — separate from <c>jobnet.log</c>
/// for the same reason <see cref="Jobnet.Services.Ai.AiLogger"/> is separate: a broken custom
/// parser is a distinct, actionable failure mode and shouldn't get lost in general app noise.
///
/// This is the "clear log message" half of surfacing a broken directory parser — the other half
/// is <c>discovery_seeds.custom_parser_last_error</c>, written by the same call site, which is
/// what the Parser Report screen actually displays. This log exists for the full exception detail
/// (stack trace) that doesn't belong in a UI grid cell.
///
/// Every write is wrapped in try/catch — logging must never break the harvest it's reporting on.
/// </summary>
public static class DirectoryParserLogger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Jobnet", "directory-parser-errors.log");

    public static void LogFailure(string parserName, string sourceName, string url, Exception ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var sb = new System.Text.StringBuilder();
            sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("] parser=").Append(parserName)
              .Append(" source=").AppendLine(sourceName);
            sb.Append("  url:       ").AppendLine(url);
            sb.Append("  exception: ").Append(ex.GetType().Name).Append(": ").AppendLine(ex.Message);
            sb.Append("  stack:").AppendLine();
            sb.AppendLine(ex.StackTrace ?? "    (no stack trace)");
            sb.AppendLine(new string('-', 60));

            File.AppendAllText(LogPath, sb.ToString());
        }
        catch
        {
            // Logging must not break the calling harvest.
        }
    }
}
