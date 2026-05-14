using System;
using Jobnet.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class MigrateCommand : ICliCommand
{
    public string Name => "migrate";
    public string Description => "Run pending database migrations (idempotent)";

    public int Run(string[] args, IServiceProvider services)
    {
        services.GetRequiredService<MigrationRunner>().Run();
        Console.WriteLine("Migrations complete.");
        return 0;
    }
}
