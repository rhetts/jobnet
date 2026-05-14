using System;
using Jobnet.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class ConfigGetCommand : ICliCommand
{
    public string Name => "config-get";
    public string Description => "Get a config value by key.  Usage: config-get <key>";

    public int Run(string[] args, IServiceProvider services)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("Usage: config-get <key>");
            return 2;
        }

        var repo = services.GetRequiredService<IConfigRepository>();
        var value = repo.Get(args[0]);
        if (value is null)
        {
            Console.WriteLine($"(not set: {args[0]})");
            return 1;
        }

        Console.WriteLine(value);
        return 0;
    }
}
