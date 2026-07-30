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
        // Gemini, Groq, OpenRouter expose OpenAI-compatible surfaces — we hit them through
        // the standard OpenAIClient with overridden endpoints. No provider-specific SDK.
        private const string GeminiOpenAIEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai/";
        private const string GroqOpenAIEndpoint = "https://api.groq.com/openai/v1";
        private const string OpenRouterOpenAIEndpoint = "https://openrouter.ai/api/v1";

        // Fallbacks used when the corresponding <PROVIDER>_MODEL env var is empty.
        private const string FallbackAnthropicModel = "claude-sonnet-4-20250514";
        private const string FallbackGeminiModel = "gemini-2.5-flash";
        private const string FallbackGroqModel = "llama-3.1-8b-instant"; // higher TPM headroom on free tier; smaller model, may loop more on tool calling
        private const string FallbackOpenRouterModel = "deepseek/deepseek-chat"; // cheap ($0.14/$0.28 per M), tools capable, good for multi-agent
        // Ollama default — qwen3-coder:30b handles the 5-agent tool-calling workflow
        // noticeably better than smaller models, at the cost of 5-15x more latency on
        // local hardware. Set OLLAMA_MODEL=qwen2.5:7b for faster local iteration when
        // you don't need plan-quality output.
        private const string FallbackOllamaModel = "qwen3-coder:30b";

        // Env-var override wrappers so operators can pin any provider's model without a
        // code change or full redeploy — e.g. flip Groq to `llama-3.3-70b-versatile` for
        // an A/B run, or point OpenRouter at `openai/gpt-4o-mini` for a cost calibration.
        private static string DefaultAnthropicModel  => Env("ANTHROPIC_MODEL",  FallbackAnthropicModel);
        private static string DefaultGeminiModel     => Env("GEMINI_MODEL",     FallbackGeminiModel);
        private static string DefaultGroqModel       => Env("GROQ_MODEL",       FallbackGroqModel);
        private static string DefaultOpenRouterModel => Env("OPEN_ROUTER_MODEL", FallbackOpenRouterModel);
        private static string DefaultOllamaModel     => Env("OLLAMA_MODEL",     FallbackOllamaModel);

        private static string Env(string key, string fallback)
            => Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;

        // Caps tool-calling rounds inside a single LLM request to stop runaway loops on
        // mid-tier models (Llama 3.3 70B on Groq, smaller Ollama models). The legitimate
        // Researcher pass uses 5-7 tool calls (Nominatim×2, Open-Meteo, OpenTripMap, OSRM);
        // 8 leaves a little headroom without letting the model burn iterations re-calling
        // the same tool. Default in Microsoft.Extensions.AI is 10. See FunctionInvokingChatClient.
        private const int MaxToolIterations = 8;

        public enum Provider
        {
            Ollama,
            Anthropic,
            Gemini,
            Groq,
            OpenRouter
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
                Provider.OpenRouter => CreateOpenRouterClient(model ?? DefaultOpenRouterModel),
                _ => throw new ArgumentException($"Unknown provider: {provider}")
            };
        }

        /// <summary>
        /// Creates an IChatClient by auto-detecting the best available provider.
        /// Precedence: explicit preference → Anthropic key → Groq key → Gemini key → Ollama (local).
        /// Groq is preferred over Gemini for free-tier work because its limits are more usable
        /// for the multi-agent tool-calling pattern; Gemini still wins when explicitly picked.
        /// </summary>
        /// <summary>
        /// Optional cheap client used exclusively for router (route classification) calls.
        /// Router only needs to pick one of 6 tokens, so a smaller model saves cost with no
        /// quality impact. Opt in via <c>ROUTER_PROVIDER</c> + <c>ROUTER_MODEL</c> env vars;
        /// returns null if neither is set (callers should fall back to the main agent client).
        /// </summary>
        public static (IChatClient Client, Provider Provider, string Model)? CreateRouterClientIfConfigured()
        {
            var providerToken = Environment.GetEnvironmentVariable("ROUTER_PROVIDER");
            var modelOverride = Environment.GetEnvironmentVariable("ROUTER_MODEL");
            if (string.IsNullOrEmpty(providerToken) && string.IsNullOrEmpty(modelOverride)) return null;

            // If only ROUTER_MODEL is set (no provider), we need a provider to satisfy the
            // factory. Fall back to the same auto-detected provider as the main client.
            Provider provider;
            if (!string.IsNullOrEmpty(providerToken))
            {
                if (!Enum.TryParse<Provider>(providerToken, ignoreCase: true, out provider))
                    return null;
            }
            else
            {
                var (_, autoProvider, _) = CreateWithAutoDetect(null);
                provider = autoProvider;
            }

            var model = modelOverride ?? provider switch
            {
                Provider.Anthropic   => "claude-haiku-4-5-20251001",
                Provider.Gemini      => "gemini-2.5-flash",
                Provider.Groq        => "llama-3.1-8b-instant",
                Provider.OpenRouter  => "openai/gpt-4o-mini",
                _                    => DefaultOllamaModel
            };

            return (Create(provider, model), provider, model);
        }

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
                        Provider.OpenRouter => DefaultOpenRouterModel,
                        _ => DefaultOllamaModel
                    };
                    return (Create(provider, model), provider, model);
                }
            }

            // Auto-detect: Anthropic → OpenRouter → Groq → Gemini → Ollama
            // OpenRouter above Groq because paid credits should be used before free-tier
            // limits kick in — user opted in by loading credit.
            var anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            if (!string.IsNullOrEmpty(anthropicKey))
            {
                return (CreateAnthropicClient(DefaultAnthropicModel), Provider.Anthropic, DefaultAnthropicModel);
            }

            var openRouterKey = Environment.GetEnvironmentVariable("OPEN_ROUTER_AI_KEY");
            if (!string.IsNullOrEmpty(openRouterKey))
            {
                return (CreateOpenRouterClient(DefaultOpenRouterModel), Provider.OpenRouter, DefaultOpenRouterModel);
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
                .Use(inner => new TokenCountingChatClient(inner))
                .Build();
        }

        private static IChatClient CreateOpenRouterClient(string model)
        {
            var apiKey = Environment.GetEnvironmentVariable("OPEN_ROUTER_AI_KEY")
                ?? throw new InvalidOperationException(
                    "OPEN_ROUTER_AI_KEY environment variable is not set. " +
                    "Get a key at https://openrouter.ai/keys");

            // OpenRouter aggregates many models behind a single OpenAI-compat endpoint.
            // Model name follows "provider/model" convention (e.g. "deepseek/deepseek-chat",
            // "anthropic/claude-3.5-sonnet", "meta-llama/llama-3.3-70b-instruct").
            var openAiClient = new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(OpenRouterOpenAIEndpoint) });

            return openAiClient
                .GetChatClient(model)
                .AsIChatClient()
                .AsBuilder()
                .Use(inner => new RetryingChatClient(inner))
                .UseFunctionInvocation(loggerFactory: null,
                    configure: fic => fic.MaximumIterationsPerRequest = MaxToolIterations)
                .Use(inner => new TokenCountingChatClient(inner))
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
                .Use(inner => new TokenCountingChatClient(inner))
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
                .Use(inner => new TokenCountingChatClient(inner))
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
                .Use(inner => new TokenCountingChatClient(inner))
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
    /// Accumulates <see cref="UsageDetails"/> across every LLM round-trip on a single
    /// <see cref="IChatClient"/>. Sits at the top of the middleware chain so it sees
    /// all sub-turns of function-calling agents (Researcher's tool loop, Auditor's tool
    /// loop, etc.) as one aggregate cost.
    ///
    /// Resolve via <c>chatClient.GetService(typeof(TokenCountingChatClient))</c> to read
    /// <see cref="InputTokens"/> / <see cref="OutputTokens"/> after the workflow completes.
    /// Because <see cref="ChatClientFactory.CreateWithAutoDetect"/> builds a fresh client
    /// per API request, the counters are naturally turn-scoped.
    /// </summary>
    public sealed class TokenCountingChatClient : DelegatingChatClient
    {
        private long _inputTokens;
        private long _outputTokens;

        public TokenCountingChatClient(IChatClient inner) : base(inner) { }

        public long InputTokens => Interlocked.Read(ref _inputTokens);
        public long OutputTokens => Interlocked.Read(ref _outputTokens);

        public void Reset()
        {
            Interlocked.Exchange(ref _inputTokens, 0);
            Interlocked.Exchange(ref _outputTokens, 0);
        }

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            Add(response.Usage);
            return response;
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Providers typically send Usage on the final update chunk (e.g. OpenAI-compat
            // servers with `stream_options.include_usage`). Sum whatever arrives.
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                if (update.Contents != null)
                {
                    foreach (var content in update.Contents)
                    {
                        if (content is UsageContent uc) Add(uc.Details);
                    }
                }
                yield return update;
            }
        }

        private void Add(UsageDetails? usage)
        {
            if (usage == null) return;
            if (usage.InputTokenCount is long input) Interlocked.Add(ref _inputTokens, input);
            if (usage.OutputTokenCount is long output) Interlocked.Add(ref _outputTokens, output);
        }

        // Expose ourself so downstream code can retrieve counters without knowing chain shape.
        public override object? GetService(Type serviceType, object? serviceKey = null)
        {
            if (serviceType == typeof(TokenCountingChatClient)) return this;
            return base.GetService(serviceType, serviceKey);
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
