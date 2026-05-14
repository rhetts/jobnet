using System;
using Jobnet.Services.Classification;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class ClassifyCommand : ICliCommand
{
    public string Name => "classify";
    public string Description => "Classify a job title.  Usage: classify \"<title>\" [--dept <dept>]";

    public int Run(string[] args, IServiceProvider services)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("Usage: classify \"<title>\" [--dept <dept>]");
            return 2;
        }

        var title = args[0];
        string? dept = null;
        for (var i = 1; i < args.Length - 1; i++)
            if (args[i] == "--dept") dept = args[i + 1];

        var classifier = services.GetRequiredService<IJobClassifier>();
        var result = classifier.Classify(title, dept);

        Console.WriteLine($"Title: {title}{(dept is null ? "" : $"  (dept: {dept})")}");
        Console.WriteLine($"Source: {result.Source}");
        Console.WriteLine($"Level:  {(result.LevelName ?? "(unknown)")} {(result.LevelId.HasValue ? $"[id={result.LevelId}]" : "")}");
        if (result.Areas.Count == 0)
            Console.WriteLine("Areas:  (none)");
        else
        {
            Console.WriteLine($"Areas:  ({result.Areas.Count})");
            foreach (var a in result.Areas)
                Console.WriteLine($"        - {a.Name} [id={a.Id}]");
        }
        Console.WriteLine($"Why:    {result.Reason}");
        return 0;
    }
}
