using System;
using System.IO;

namespace Jobnet.Services;

public interface IAppPaths
{
    string DataDirectory { get; }
    string DatabasePath { get; }
}

public sealed class AppPaths : IAppPaths
{
    public string DataDirectory { get; }
    public string DatabasePath { get; }

    public AppPaths()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        DataDirectory = Path.Combine(localAppData, "Jobnet");
        Directory.CreateDirectory(DataDirectory);
        DatabasePath = Path.Combine(DataDirectory, "jobnet.db");
    }
}
