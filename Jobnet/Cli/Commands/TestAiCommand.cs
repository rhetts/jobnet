using System;
using Jobnet.Services.Ai;
using Microsoft.Extensions.DependencyInjection;

namespace Jobnet.Cli.Commands;

public sealed class TestAiCommand : ICliCommand
{
    public string Name => "test-ai";
    public string Description => "Verify the configured AI provider (gemini or claude) by sending a tiny test prompt.";

    public int Run(string[] args, IServiceProvider services)
    {
        var ai = services.GetRequiredService<IAiClient>();
        if (!ai.IsConfigured)
        {
            Console.WriteLine($"AI provider '{ai.ProviderId}' is not configured.");
            Console.WriteLine();
            if (ai.ProviderId == "gemini")
            {
                Console.WriteLine("To get a free Gemini API key:");
                Console.WriteLine("  1. Go to https://aistudio.google.com/apikey");
                Console.WriteLine("  2. Sign in with a Google account");
                Console.WriteLine("  3. Click \"Create API key\"");
                Console.WriteLine("  4. Copy the key (starts with 'AIzaSy')");
                Console.WriteLine();
                Console.WriteLine("Then: Jobnet.exe config-set gemini_api_key <your-key>");
            }
            else
            {
                Console.WriteLine("To get a Claude API key:");
                Console.WriteLine("  1. Go to https://console.anthropic.com/settings/keys");
                Console.WriteLine("  2. Create a key");
                Console.WriteLine("  3. Jobnet.exe config-set claude_api_key <your-key>");
            }
            return 1;
        }

        Console.WriteLine($"Sending test prompt to {ai.ProviderId}...");
        try
        {
            var response = ai.CompleteAsync(
                "Reply with exactly: \"Jobnet test OK\"",
                system: "You are a test endpoint. Reply with the exact phrase requested, nothing else.",
                maxTokens: 32).GetAwaiter().GetResult();

            Console.WriteLine($"Provider:      {response.ProviderId}");
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
