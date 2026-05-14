using System;
using Jobnet.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class LevelsListCommand : ICliCommand
{
    public string Name => "levels-list";
    public string Description => "List all levels in priority order";

    public int Run(string[] args, IServiceProvider services)
    {
        var repo = services.GetRequiredService<ILevelRepository>();
        var list = repo.GetAll();
        Console.WriteLine($"{"ID",-4}  {"Order",-5}  Name");
        Console.WriteLine(new string('-', 40));
        foreach (var l in list)
            Console.WriteLine($"{l.Id,-4}  {l.SortOrder,-5}  {l.Name}");
        Console.WriteLine($"\n{list.Count} levels.");
        return 0;
    }
}
