using System;
using Jobnet.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class ConfigListCommand : ICliCommand
{
    public string Name => "config-list";
    public string Description => "List all config key/value pairs";

    public int Run(string[] args, IServiceProvider services)
    {
        var repo = services.GetRequiredService<IConfigRepository>();
        var all = repo.GetAll();

        Console.WriteLine($"Config ({all.Count} keys):");
        var keyWidth = 0;
        foreach (var k in all.Keys)
            if (k.Length > keyWidth) keyWidth = k.Length;

        foreach (var (k, v) in all)
        {
            var display = string.IsNullOrEmpty(v) ? "(empty)" : v;
            if (display.Length > 80) display = display.Substring(0, 77) + "...";
            Console.WriteLine($"  {k.PadRight(keyWidth)}  {display}");
        }
        return 0;
    }
}
