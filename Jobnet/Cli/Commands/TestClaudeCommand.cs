using System;
using Jobnet.Services.Claude;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class TestClaudeCommand : ICliCommand
{
    public string Name => "test-claude";
    public string Description => "Verify Claude API key works by sending a tiny test prompt.";

    public int Run(string[] args, IServiceProvider services)
    {
        var claude = services.GetRequiredService<IClaudeClient>();
        if (!claude.IsConfigured)
        {
            Console.WriteLine("Claude API key is not configured.");
            Console.WriteLine("Get a key at https://console.anthropic.com/settings/keys");
            Console.WriteLine("Then: Jobnet.exe config-set claude_api_key <your-key>");
            return 1;
        }

        Console.WriteLine("Sending test prompt to Claude...");
        try
        {
            var response = claude.CompleteAsync(
                "Reply with exactly: \"Jobnet test OK\"",
                system: "You are a test endpoint. Reply with the exact phrase requested, nothing else.",
                maxTokens: 32).GetAwaiter().GetResult();

            Console.WriteLine($"Model:         {response.Model}");
            Console.WriteLine($"Input tokens:  {response.InputTokens}");
            Console.WriteLine($"Output tokens: {response.OutputTokens}");
            Console.WriteLine($"Response:      {response.Text.Trim()}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED: {ex.Message}");
            return 1;
        }
    }
}
