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

        var fileWriter = new StreamWriter(LogPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        fileWriter.WriteLine($"=== {DateTime.Now:O} ===");

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
