using System;
using Jobnet.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class ConfigSetCommand : ICliCommand
{
    public string Name => "config-set";
    public string Description => "Set a config value.  Usage: config-set <key> <value>";

    public int Run(string[] args, IServiceProvider services)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: config-set <key> <value>");
            return 2;
        }

        var repo = services.GetRequiredService<IConfigRepository>();
        repo.Set(args[0], args[1]);
        Console.WriteLine($"Set {args[0]} = {args[1]}");
        return 0;
    }
}
