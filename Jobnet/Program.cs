using System;
using Jobnet.Cli;

namespace Jobnet;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0)
            return CliRunner.Run(args);

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
