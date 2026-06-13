using System.IO;

namespace Jobnet.Tests.Fixtures;

/// <summary>
/// Loads HTML fixture files from the test output directory. Fixtures are real captures from
/// company careers pages — saved verbatim so parsers are tested against actual production
/// markup, not artisanal hand-written snippets that pass by construction.
///
/// To capture a fresh fixture: <c>Jobnet.exe derive-parser --company &lt;domain&gt; --dump-html
/// Jobnet.Tests/Fixtures/&lt;name&gt;.html --no-persist</c>. The csproj has a CopyToOutputDirectory
/// rule for everything under Fixtures/, so new files are picked up automatically.
/// </summary>
internal static class FixtureLoader
{
    /// <summary>Read a fixture by file name (relative to the Fixtures directory).</summary>
    public static string Load(string fileName)
    {
        var path = Path.Combine(BaseDir, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Fixture not found: {path}. " +
                $"Capture one with: Jobnet.exe derive-parser --company <domain> --dump-html {path} --no-persist",
                path);
        return File.ReadAllText(path);
    }

    /// <summary>The Fixtures directory next to the running test assembly. csproj copies fixtures
    /// here at build time.</summary>
    public static string BaseDir =>
        Path.Combine(Path.GetDirectoryName(typeof(FixtureLoader).Assembly.Location)!, "Fixtures");
}
