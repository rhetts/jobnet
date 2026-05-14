using System;

namespace Jobnet.Cli;

public interface ICliCommand
{
    string Name { get; }
    string Description { get; }
    int Run(string[] args, IServiceProvider services);
}
