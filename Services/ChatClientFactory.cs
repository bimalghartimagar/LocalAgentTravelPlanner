using Anthropic.SDK;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace LocalAgentTravelPlanner.Services
{
    /// <summary>
    /// Factory for creating IChatClient instances from different providers.
    ///
    /// PROVIDER ABSTRACTION:
    /// Both Ollama and Anthropic implement IChatClient from Microsoft.Extensions.AI,
    /// so you can switch providers with a simple configuration change.
    ///
    /// USAGE:
    /// - Set environment variable ANTHROPIC_API_KEY for Claude
    /// - Or use Ollama (no API key needed, runs locally)
    /// </summary>
    public static class ChatClientFactory
    {
        public enum Provider
        {
            Ollama,
            Anthropic
        }

        /// <summary>
        /// Creates an IChatClient based on the specified provider.
        /// </summary>
        public static IChatClient Create(Provider provider, string? model = null)
        {
            return provider switch
            {
                Provider.Ollama => CreateOllamaClient(model ?? "qwen2.5:7b"),
                Provider.Anthropic => CreateAnthropicClient(model ?? "claude-sonnet-4-20250514"),
                _ => throw new ArgumentException($"Unknown provider: {provider}")
            };
        }

        /// <summary>
        /// Creates an IChatClient by auto-detecting the best available provider.
        /// Prefers Anthropic if API key is set, otherwise falls back to Ollama.
        /// </summary>
        public static (IChatClient Client, Provider Provider, string Model) CreateWithAutoDetect(
            string? preferredProvider = null)
        {
            // Check if user specified a preference
            if (!string.IsNullOrEmpty(preferredProvider))
            {
                if (Enum.TryParse<Provider>(preferredProvider, ignoreCase: true, out var provider))
                {
                    var model = provider == Provider.Anthropic
                        ? "claude-sonnet-4-20250514"
                        : "qwen2.5:7b";
                    return (Create(provider, model), provider, model);
                }
            }

            // Auto-detect: prefer Anthropic if API key exists
            var anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            if (!string.IsNullOrEmpty(anthropicKey))
            {
                const string model = "claude-sonnet-4-20250514";
                return (CreateAnthropicClient(model), Provider.Anthropic, model);
            }

            // Fall back to Ollama (local)
            const string ollamaModel = "qwen2.5:7b";
            return (CreateOllamaClient(ollamaModel), Provider.Ollama, ollamaModel);
        }

        private static IChatClient CreateOllamaClient(string model)
        {
            var ollamaUri = Environment.GetEnvironmentVariable("OLLAMA_HOST")
                ?? "http://localhost:11434";

            IChatClient baseClient = new OllamaApiClient(new Uri(ollamaUri), model);

            // Wrap with function invocation support
            return new ChatClientBuilder(baseClient)
                .UseFunctionInvocation()
                .Build();
        }

        private static IChatClient CreateAnthropicClient(string model)
        {
            var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                ?? throw new InvalidOperationException(
                    "ANTHROPIC_API_KEY environment variable is not set. " +
                    "Set it with: set ANTHROPIC_API_KEY=your-api-key");

            // Create Anthropic client - it implements IChatClient
            var anthropicClient = new AnthropicClient(apiKey);

            // Get the IChatClient from the Anthropic client
            // The Messages property provides IChatClient implementation
            IChatClient baseClient = anthropicClient.Messages;

            // Wrap with function invocation support
            return new ChatClientBuilder(baseClient)
                .UseFunctionInvocation()
                .Build();
        }
    }
}
