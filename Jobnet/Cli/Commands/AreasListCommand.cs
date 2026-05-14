using System;
using Jobnet.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class AreasListCommand : ICliCommand
{
    public string Name => "areas-list";
    public string Description => "List all areas in priority order";

    public int Run(string[] args, IServiceProvider services)
    {
        var repo = services.GetRequiredService<IAreaRepository>();
        var list = repo.GetAll();
        Console.WriteLine($"{"ID",-4}  {"Order",-5}  Name");
        Console.WriteLine(new string('-', 40));
        foreach (var a in list)
            Console.WriteLine($"{a.Id,-4}  {a.SortOrder,-5}  {a.Name}");
        Console.WriteLine($"\n{list.Count} areas.");
        return 0;
    }
}
