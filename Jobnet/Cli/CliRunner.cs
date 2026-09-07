using System;
using System.Collections.Generic;
using System.Linq;
using Jobnet.Data;
using Jobnet.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli;

internal static class CliRunner
{
    public static int Run(string[] args)
    {
        try { CliConsole.Initialize(); }
        catch (Exception ex)
        {
            // Initialize() already guards its own file I/O — this only catches something more
            // fundamental (e.g. LocalApplicationData unavailable). Console output alone still
            // works via the default Console.Out, so keep going rather than crash unhandled.
            Console.WriteLine($"Warning: CLI log initialization failed: {ex.GetType().Name}: {ex.Message}");
        }

        var services = BuildServices();
        var commands = DiscoverCommands(services).ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            PrintHelp(commands.Values);
            return 0;
        }

        var name = args[0];
        var rest = args.Skip(1).ToArray();

        if (!commands.TryGetValue(name, out var cmd))
        {
            Console.WriteLine($"Unknown command: {name}");
            Console.WriteLine();
            PrintHelp(commands.Values);
            return 2;
        }

        try
        {
            // Run pending migrations before any command (except `migrate`, which does it itself).
            if (cmd.Name != "migrate")
                services.GetRequiredService<MigrationRunner>().Run();

            return cmd.Run(rest, services);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static IServiceProvider BuildServices()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddJobnetCore();
        return sc.BuildServiceProvider();
    }

    private static IEnumerable<ICliCommand> DiscoverCommands(IServiceProvider services)
    {
        var asm = typeof(CliRunner).Assembly;
        foreach (var type in asm.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface) continue;
            if (!typeof(ICliCommand).IsAssignableFrom(type)) continue;

            var instance = ActivatorUtilities.CreateInstance(services, type) as ICliCommand;
            if (instance is not null) yield return instance;
        }
    }

    private static void PrintHelp(IEnumerable<ICliCommand> commands)
    {
        Console.WriteLine("Jobnet — CLI mode");
        Console.WriteLine();
        Console.WriteLine("Usage: Jobnet.exe <command> [args...]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        foreach (var c in commands.OrderBy(c => c.Name, StringComparer.Ordinal))
            Console.WriteLine($"  {c.Name,-20}  {c.Description}");
        Console.WriteLine();
        Console.WriteLine($"Output is mirrored to: {CliConsole.LogPath}");
    }
}
