using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Jobnet.Cli;

internal static class CliConsole
{
    private const int ATTACH_PARENT_PROCESS = -1;
    private const int STD_OUTPUT_HANDLE = -11;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    public static string LogPath { get; private set; } = string.Empty;

    public static void Initialize()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(localAppData, "Jobnet");
        Directory.CreateDirectory(dir);
        LogPath = Path.Combine(dir, "cli-output.log");

        // Attach to parent console (PowerShell/cmd) so output is visible live.
        // If launched without a parent console (e.g. double-clicked), AttachConsole fails — we still mirror to file.
        AttachConsole(ATTACH_PARENT_PROCESS);

        var stdHandle = GetStdHandle(STD_OUTPUT_HANDLE);
        TextWriter consoleWriter;
        try
        {
            if (stdHandle != IntPtr.Zero && stdHandle.ToInt64() != -1)
            {
                var stream = Console.OpenStandardOutput();
                consoleWriter = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            }
            else
            {
                consoleWriter = TextWriter.Null;
            }
        }
        catch
        {
            consoleWriter = TextWriter.Null;
        }

        // Append (not overwrite) so a run's output survives the next invocation — this used to
        // truncate the whole log on every command, so only the very last run was ever visible.
        // FileShare.ReadWrite | Delete matches the fix already applied to jobnet.log: without it,
        // a second Jobnet.exe process (concurrent CLI invocation, or a lingering GUI instance)
        // throws IOException here before the command even runs. Wrapped in try/catch so a file
        // failure degrades to console-only output instead of crashing CLI startup outright.
        TextWriter fileWriter;
        try
        {
            var fileStream = new FileStream(LogPath, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            fileWriter = new StreamWriter(fileStream, new UTF8Encoding(false)) { AutoFlush = true };
            fileWriter.WriteLine($"\n=== {DateTime.Now:O} ===");
        }
        catch
        {
            fileWriter = TextWriter.Null;
        }

        Console.SetOut(new TeeTextWriter(consoleWriter, fileWriter));
        Console.SetError(Console.Out);
    }

    private sealed class TeeTextWriter : TextWriter
    {
        private readonly TextWriter _a;
        private readonly TextWriter _b;

        public TeeTextWriter(TextWriter a, TextWriter b) { _a = a; _b = b; }

        public override Encoding Encoding => _b.Encoding;

        public override void Write(char value)         { _a.Write(value); _b.Write(value); }
        public override void Write(string? value)      { _a.Write(value); _b.Write(value); }
        public override void WriteLine()               { _a.WriteLine(); _b.WriteLine(); }
        public override void WriteLine(string? value)  { _a.WriteLine(value); _b.WriteLine(value); }
        public override void Flush()                   { _a.Flush(); _b.Flush(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _a.Dispose(); _b.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
