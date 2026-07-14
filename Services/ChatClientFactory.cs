using System.ClientModel;
using Anthropic.SDK;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;

namespace LocalAgentTravelPlanner.Services
{
    /// <summary>
    /// Factory for creating IChatClient instances from different providers.
    ///
    /// PROVIDER ABSTRACTION:
    /// All providers implement IChatClient from Microsoft.Extensions.AI, so you can
    /// switch providers with a simple configuration change.
    ///
    /// USAGE:
    /// - Set ANTHROPIC_API_KEY for Claude
    /// - Set GROQ_API_KEY for Groq-hosted open-source models (free tier, fast)
    /// - Set GEMINI_API_KEY for Google Gemini
    /// - Or use Ollama (no API key needed, runs locally)
    /// </summary>
    public static class ChatClientFactory
    {
        // Both Gemini and Groq expose OpenAI-compatible surfaces — we hit them through
        // the standard OpenAIClient with overridden endpoints. No provider-specific SDK.
        private const string GeminiOpenAIEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai/";
        private const string GroqOpenAIEndpoint = "https://api.groq.com/openai/v1";

        private const string DefaultAnthropicModel = "claude-sonnet-4-20250514";
        private const string DefaultGeminiModel = "gemini-2.5-flash";
        private const string DefaultGroqModel = "llama-3.3-70b-versatile"; // best free-tier tools model

        // Ollama default — overridable via OLLAMA_MODEL env var. qwen3-coder:30b handles
        // the 5-agent tool-calling workflow noticeably better than smaller models, at the
        // cost of 5-15x more latency on local hardware. Set to qwen2.5:7b for faster
        // local iteration when you don't need plan-quality output.
        private const string FallbackOllamaModel = "qwen3-coder:30b";

        // Caps tool-calling rounds inside a single LLM request to stop runaway loops on
        // mid-tier models (Llama 3.3 70B on Groq, smaller Ollama models). The legitimate
        // Researcher pass uses 5-7 tool calls (Nominatim×2, Open-Meteo, OpenTripMap, OSRM);
        // 8 leaves a little headroom without letting the model burn iterations re-calling
        // the same tool. Default in Microsoft.Extensions.AI is 10. See FunctionInvokingChatClient.
        private const int MaxToolIterations = 8;
        private static string DefaultOllamaModel =>
            Environment.GetEnvironmentVariable("OLLAMA_MODEL") is { Length: > 0 } v
                ? v
                : FallbackOllamaModel;

        public enum Provider
        {
            Ollama,
            Anthropic,
            Gemini,
            Groq
        }

        /// <summary>
        /// Creates an IChatClient based on the specified provider.
        /// </summary>
        public static IChatClient Create(Provider provider, string? model = null)
        {
            return provider switch
            {
                Provider.Ollama => CreateOllamaClient(model ?? DefaultOllamaModel),
                Provider.Anthropic => CreateAnthropicClient(model ?? DefaultAnthropicModel),
                Provider.Gemini => CreateGeminiClient(model ?? DefaultGeminiModel),
                Provider.Groq => CreateGroqClient(model ?? DefaultGroqModel),
                _ => throw new ArgumentException($"Unknown provider: {provider}")
            };
        }

        /// <summary>
        /// Creates an IChatClient by auto-detecting the best available provider.
        /// Precedence: explicit preference → Anthropic key → Groq key → Gemini key → Ollama (local).
        /// Groq is preferred over Gemini for free-tier work because its limits are more usable
        /// for the multi-agent tool-calling pattern; Gemini still wins when explicitly picked.
        /// </summary>
        public static (IChatClient Client, Provider Provider, string Model) CreateWithAutoDetect(
            string? preferredProvider = null)
        {
            // Explicit preference wins
            if (!string.IsNullOrEmpty(preferredProvider))
            {
                if (Enum.TryParse<Provider>(preferredProvider, ignoreCase: true, out var provider))
                {
                    var model = provider switch
                    {
                        Provider.Anthropic => DefaultAnthropicModel,
                        Provider.Gemini => DefaultGeminiModel,
                        Provider.Groq => DefaultGroqModel,
                        _ => DefaultOllamaModel
                    };
                    return (Create(provider, model), provider, model);
                }
            }

            // Auto-detect: Anthropic → Groq → Gemini → Ollama
            var anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            if (!string.IsNullOrEmpty(anthropicKey))
            {
                return (CreateAnthropicClient(DefaultAnthropicModel), Provider.Anthropic, DefaultAnthropicModel);
            }

            var groqKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
            if (!string.IsNullOrEmpty(groqKey))
            {
                return (CreateGroqClient(DefaultGroqModel), Provider.Groq, DefaultGroqModel);
            }

            var geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
            if (!string.IsNullOrEmpty(geminiKey))
            {
                return (CreateGeminiClient(DefaultGeminiModel), Provider.Gemini, DefaultGeminiModel);
            }

            return (CreateOllamaClient(DefaultOllamaModel), Provider.Ollama, DefaultOllamaModel);
        }

        private static IChatClient CreateOllamaClient(string model)
        {
            var ollamaUri = Environment.GetEnvironmentVariable("OLLAMA_HOST")
                ?? "http://localhost:11434";

            IChatClient baseClient = new OllamaApiClient(new Uri(ollamaUri), model);

            // Wrap with function invocation support
            return new ChatClientBuilder(baseClient)
                .UseFunctionInvocation(loggerFactory: null,
                    configure: fic => fic.MaximumIterationsPerRequest = MaxToolIterations)
                .Build();
        }

        private static IChatClient CreateGroqClient(string model)
        {
            var apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY")
                ?? throw new InvalidOperationException(
                    "GROQ_API_KEY environment variable is not set. " +
                    "Get a free key at https://console.groq.com/keys");

            // Groq exposes the same OpenAI-compatible surface as Gemini, just at a different
            // endpoint. Inference is unusually fast (~500 tokens/sec on Llama 70B) and the
            // free tier is genuinely usable for multi-agent workloads.
            var openAiClient = new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(GroqOpenAIEndpoint) });

            return openAiClient
                .GetChatClient(model)
                .AsIChatClient()
                .AsBuilder()
                .Use(inner => new RetryingChatClient(inner))
                .UseFunctionInvocation(loggerFactory: null,
                    configure: fic => fic.MaximumIterationsPerRequest = MaxToolIterations)
                .Build();
        }

        private static IChatClient CreateGeminiClient(string model)
        {
            var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                ?? throw new InvalidOperationException(
                    "GEMINI_API_KEY environment variable is not set. " +
                    "Get a key at https://aistudio.google.com/app/apikey");

            // Gemini's OpenAI-compat endpoint accepts the standard OpenAIClient with the
            // Endpoint overridden. Tool calling and streaming both work through this surface.
            var openAiClient = new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(GeminiOpenAIEndpoint) });

            return openAiClient
                .GetChatClient(model)
                .AsIChatClient()
                .AsBuilder()
                // Retry transient 429/503 first so a single rate-limited call doesn't kill
                // the whole turn. Has to sit BEFORE UseFunctionInvocation so it retries each
                // individual LLM round-trip during tool calling, not just the outer call.
                .Use(inner => new RetryingChatClient(inner))
                .UseFunctionInvocation(loggerFactory: null,
                    configure: fic => fic.MaximumIterationsPerRequest = MaxToolIterations)
                .Build();
        }

        private static IChatClient CreateAnthropicClient(string model)
        {
            var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                ?? throw new InvalidOperationException(
                    "ANTHROPIC_API_KEY environment variable is not set. " +
                    "Set it with: set ANTHROPIC_API_KEY=your-api-key");

            var anthropicClient = new AnthropicClient(apiKey);
            IChatClient baseClient = anthropicClient.Messages;

            // Wrap with middleware that injects ModelId and MaxOutputTokens into every request.
            // Anthropic.SDK requires both per-request (unlike Ollama which bakes model
            // into the constructor and doesn't require max_tokens). MAF doesn't pass
            // these in ChatOptions, so we inject them via middleware.
            var modelToInject = model;
            return new ChatClientBuilder(baseClient)
                .Use(inner => new AnthropicOptionsInjector(inner, modelToInject))
                .UseFunctionInvocation(loggerFactory: null,
                    configure: fic => fic.MaximumIterationsPerRequest = MaxToolIterations)
                .Build();
        }
    }

    /// <summary>
    /// Retries transient failures (HTTP 429 / 5xx) from upstream LLM providers with
    /// exponential backoff + jitter. Sits between the function-invocation layer and the
    /// transport so each individual LLM round-trip (including tool-calling sub-turns)
    /// gets its own retry budget.
    ///
    /// Catches both <see cref="System.ClientModel.ClientResultException"/> (OpenAI SDK)
    /// and <see cref="HttpRequestException"/> for transport-layer hiccups.
    /// </summary>
    internal class RetryingChatClient : DelegatingChatClient
    {
        private const int MaxRetries = 3;
        private static readonly Random Jitter = new();
        private static readonly int[] RetryableStatusCodes = { 408, 429, 500, 502, 503, 504 };

        public RetryingChatClient(IChatClient inner) : base(inner) { }

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            // Materialize once so each retry sees the same message list.
            var materialized = messages as IList<ChatMessage> ?? messages.ToList();

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await base.GetResponseAsync(materialized, options, cancellationToken);
                }
                catch (Exception ex) when (attempt < MaxRetries && IsTransient(ex))
                {
                    await DelayAsync(attempt, cancellationToken);
                }
            }
        }

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            // Streaming retries are tricky because partial output may already have been
            // surfaced. For now, defer to the inner client; the workflow loop will see
            // the failure and fall through to error handling. Function-invocation calls
            // inside an agent turn use the non-streaming path, so they still benefit.
            return base.GetStreamingResponseAsync(messages, options, cancellationToken);
        }

        private static bool IsTransient(Exception ex)
        {
            // OpenAI SDK surfaces HTTP errors as ClientResultException with a Status property
            if (ex is System.ClientModel.ClientResultException clientEx)
                return Array.IndexOf(RetryableStatusCodes, clientEx.Status) >= 0;

            // Transport-layer issues — network blip, DNS hiccup, etc.
            if (ex is HttpRequestException) return true;
            if (ex is TaskCanceledException tce && tce.InnerException is TimeoutException) return true;

            return false;
        }

        private static Task DelayAsync(int attempt, CancellationToken cancellationToken)
        {
            // Exponential backoff: 1s, 2s, 4s base + 0-500ms jitter
            var baseMs = 1000 * (int)Math.Pow(2, attempt);
            var jitterMs = Jitter.Next(0, 500);
            return Task.Delay(baseMs + jitterMs, cancellationToken);
        }
    }

    /// <summary>
    /// Middleware that ensures ChatOptions.ModelId and MaxOutputTokens are always set.
    /// Required for Anthropic.SDK because:
    /// 1. MAF calls IChatClient without specifying a model, but Anthropic requires it per-request.
    /// 2. Anthropic API requires max_tokens for every request (no default fallback).
    /// </summary>
    internal class AnthropicOptionsInjector : DelegatingChatClient
    {
        private readonly string _model;
        private const int DefaultMaxOutputTokens = 8192;

        public AnthropicOptionsInjector(IChatClient inner, string model) : base(inner)
        {
            _model = model;
        }

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            options = EnsureRequiredOptions(options);
            return await base.GetResponseAsync(messages, options, cancellationToken);
        }

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            options = EnsureRequiredOptions(options);
            return base.GetStreamingResponseAsync(messages, options, cancellationToken);
        }

        private ChatOptions EnsureRequiredOptions(ChatOptions? options)
        {
            options ??= new ChatOptions();
            if (string.IsNullOrEmpty(options.ModelId)) options.ModelId = _model;
            if (options.MaxOutputTokens == null) options.MaxOutputTokens = DefaultMaxOutputTokens;
            return options;
        }
    }
}
