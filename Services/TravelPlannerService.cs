using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using LocalAgentTravelPlanner.Agents;
using LocalAgentTravelPlanner.Services.Conversations;
using LocalAgentTravelPlanner.Tools;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalAgentTravelPlanner.Services
{
    /// <summary>
    /// Implementation of the travel planning service.
    ///
    /// ARCHITECTURE PATTERN: Service Layer
    /// This class encapsulates the entire agent workflow, making it:
    /// 1. Reusable: Console app, Web API, Azure Function - all can use this
    /// 2. Testable: Can inject mock IChatClient for testing
    /// 3. Configurable: Model/endpoint can be injected via DI
    ///
    /// DEPENDENCY INJECTION:
    /// The IChatClient is injected, not created here. This follows the
    /// "Dependency Inversion Principle" - high-level modules (this service)
    /// shouldn't depend on low-level modules (Ollama/OpenAI specifics).
    /// </summary>
    public class TravelPlannerService : ITravelPlannerService
    {
        private static readonly Dictionary<string, string> ExecutorMap = new()
        {
            ["Researcher_Agent"] = "Researcher",
            ["Planner_Agent"] = "Planner",
            ["Accountant_Agent"] = "Accountant",
            ["Auditor_Agent"] = "Auditor",
            ["Aggregator_Agent"] = "Aggregator",
        };

        private readonly IChatClient _chatClient;
        private readonly ILogger<TravelPlannerService> _logger;
        private readonly ChatClientAgent _researcher;
        private readonly ChatClientAgent _planner;
        private readonly ChatClientAgent _accountant;
        private readonly ChatClientAgent _auditor;
        private readonly ChatClientAgent _aggregator;

        /// <summary>
        /// Creates a new TravelPlannerService with the specified chat client.
        ///
        /// WHY INJECT IChatClient?
        /// - In production: Pass Azure OpenAI or Anthropic client
        /// - In development: Pass Ollama client
        /// - In tests: Pass a mock client that returns predictable responses
        ///
        /// <paramref name="logger"/> is optional; when omitted, a <see cref="NullLogger{T}"/> is used.
        /// Tests that don't care about log output can use the no-logger overload.
        /// </summary>
        public TravelPlannerService(
            IChatClient chatClient,
            ResearchTools researchTools,
            TravelTools travelTools,
            ILogger<TravelPlannerService>? logger = null)
        {
            _chatClient = chatClient;
            _logger = logger ?? NullLogger<TravelPlannerService>.Instance;

            // Initialize all agents using the factory pattern
            // Factories encapsulate agent configuration (prompts, tools)
            _researcher = ResearcherAgentFactory.Create(_chatClient, researchTools, travelTools);
            _planner = PlannerAgentFactory.Create(_chatClient);
            _accountant = AccountantAgentFactory.Create(_chatClient);
            _auditor = AuditorAgentFactory.Create(_chatClient);
            _aggregator = AggregatorAgentFactory.Create(_chatClient);
        }

        /// <inheritdoc/>
        public async Task<TravelPlanResponse> PlanTravelAsync(
            string request,
            CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Pre-validate intent before committing to the 5-agent pipeline
                if (!await IsTravelRelatedAsync(request, cancellationToken))
                {
                    stopwatch.Stop();
                    return new TravelPlanResponse
                    {
                        Success = false,
                        TravelPlan = NotTravelRefusal,
                        ProcessingTime = stopwatch.Elapsed,
                        AgentsUsed = 0
                    };
                }

                // Build the sequential workflow
                var workflow = AgentWorkflowBuilder.BuildSequential(
                    new List<ChatClientAgent> { _researcher, _planner, _accountant, _auditor, _aggregator }
                );

                // Execute and collect all output
                var outputBuilder = new System.Text.StringBuilder();

                StreamingRun run = await InProcessExecution.StreamAsync(workflow, request);
                await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

                string? agentError = null;
                var completedAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                await foreach (WorkflowEvent evt in run.WatchStreamAsync().WithCancellation(cancellationToken))
                {
                    if (evt is AgentRunUpdateEvent e)
                    {
                        var content = e.Data?.ToString();
                        if (!string.IsNullOrEmpty(content))
                            outputBuilder.Append(content);
                    }
                    else if (evt is ExecutorCompletedEvent completedEvt)
                    {
                        var resolved = TryResolveAgent(completedEvt.ExecutorId);
                        if (resolved != null)
                        {
                            completedAgents.Add(resolved);
                        }
                    }
                    else if (evt is ExecutorFailedEvent failedEvt)
                    {
                        var ex = failedEvt.Data as Exception;
                        agentError = $"Agent {failedEvt.ExecutorId} failed: {ex?.InnerException?.Message ?? ex?.Message ?? "unknown error"}";
                    }
                    else if (evt is WorkflowErrorEvent errorEvt)
                    {
                        var ex = errorEvt.Data as Exception;
                        agentError = $"Workflow error: {ex?.InnerException?.Message ?? ex?.Message ?? "unknown error"}";
                        break;
                    }
                    else if (evt is WorkflowOutputEvent)
                    {
                        break;
                    }
                }

                stopwatch.Stop();

                var output = outputBuilder.ToString();
                var success = agentError == null && !string.IsNullOrWhiteSpace(output);

                return new TravelPlanResponse
                {
                    Success = success,
                    TravelPlan = output,
                    Error = agentError,
                    ProcessingTime = stopwatch.Elapsed,
                    AgentsUsed = completedAgents.Count
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new TravelPlanResponse
                {
                    Success = false,
                    TravelPlan = string.Empty,
                    Error = ex.Message,
                    ProcessingTime = stopwatch.Elapsed,
                    AgentsUsed = 0
                };
            }
        }

        /// <inheritdoc/>
        public async IAsyncEnumerable<TravelPlanProgress> PlanTravelStreamingAsync(
            string request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Pre-validate intent before committing to the 5-agent pipeline
            if (!await IsTravelRelatedAsync(request, cancellationToken))
            {
                yield return new TravelPlanProgress
                {
                    CurrentAgent = "Complete",
                    Status = ProgressStatus.Error,
                    PartialOutput = NotTravelRefusal,
                    ProgressPercent = 0
                };
                yield break;
            }

            var agentNames = new[] { "Researcher", "Planner", "Accountant", "Auditor", "Aggregator" };
            var currentAgent = agentNames[0];

            int GetAgentIndex(string name) => Array.IndexOf(agentNames, name);

            // Build the sequential workflow
            var workflow = AgentWorkflowBuilder.BuildSequential(
                new List<ChatClientAgent> { _researcher, _planner, _accountant, _auditor, _aggregator }
            );

            yield return new TravelPlanProgress
            {
                CurrentAgent = currentAgent,
                Status = ProgressStatus.Starting,
                ProgressPercent = 0
            };

            StreamingRun run = await InProcessExecution.StreamAsync(workflow, request);
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

            await foreach (WorkflowEvent evt in run.WatchStreamAsync().WithCancellation(cancellationToken))
            {
                if (evt is ExecutorInvokedEvent invokedEvt)
                {
                    var resolved = TryResolveAgent(invokedEvt.ExecutorId);
                    if (resolved == null) continue;
                    currentAgent = resolved;

                    yield return new TravelPlanProgress
                    {
                        CurrentAgent = currentAgent,
                        Status = ProgressStatus.Starting,
                        ProgressPercent = (GetAgentIndex(currentAgent) * 100) / agentNames.Length
                    };
                }
                else if (evt is AgentRunUpdateEvent e)
                {
                    var content = e.Data?.ToString();
                    if (string.IsNullOrEmpty(content)) continue;

                    yield return new TravelPlanProgress
                    {
                        CurrentAgent = currentAgent,
                        Status = ProgressStatus.Processing,
                        PartialOutput = content,
                        ProgressPercent = (GetAgentIndex(currentAgent) * 100) / agentNames.Length
                    };
                }
                else if (evt is ExecutorCompletedEvent completedEvt)
                {
                    var resolved = TryResolveAgent(completedEvt.ExecutorId);
                    if (resolved == null) continue;

                    yield return new TravelPlanProgress
                    {
                        CurrentAgent = resolved,
                        Status = ProgressStatus.Completed,
                        ProgressPercent = ((GetAgentIndex(resolved) + 1) * 100) / agentNames.Length
                    };
                }
                else if (evt is ExecutorFailedEvent failedEvt)
                {
                    var resolved = TryResolveAgent(failedEvt.ExecutorId);
                    if (resolved == null) continue;
                    var ex = failedEvt.Data as Exception;

                    yield return new TravelPlanProgress
                    {
                        CurrentAgent = resolved,
                        Status = ProgressStatus.Error,
                        PartialOutput = ex?.InnerException?.Message ?? ex?.Message ?? "Agent failed",
                        ProgressPercent = (GetAgentIndex(resolved) * 100) / agentNames.Length
                    };
                }
                else if (evt is WorkflowErrorEvent errorEvt)
                {
                    var ex = errorEvt.Data as Exception;

                    yield return new TravelPlanProgress
                    {
                        CurrentAgent = currentAgent,
                        Status = ProgressStatus.Error,
                        PartialOutput = ex?.InnerException?.Message ?? ex?.Message ?? "Workflow error",
                        ProgressPercent = 0
                    };
                    break;
                }
                else if (evt is WorkflowOutputEvent)
                {
                    yield return new TravelPlanProgress
                    {
                        CurrentAgent = "Complete",
                        Status = ProgressStatus.Completed,
                        ProgressPercent = 100
                    };
                    break;
                }
            }
        }

        private const string IntentCheckPrompt = """
            You are an intent classifier. Determine if the following user message is a travel planning request.

            A valid travel request mentions any of: a destination, trip duration, travel budget, travel style,
            or asks for help planning a trip or journey.

            Respond with exactly one word: "yes" if it is travel-related, "no" if it is not.
            Do not explain. Do not add punctuation.
            """;

        /// <summary>
        /// Lightweight LLM call to check if the user's request is travel-related
        /// before committing to the full 5-agent pipeline.
        /// </summary>
        private async Task<bool> IsTravelRelatedAsync(string request, CancellationToken cancellationToken)
        {
            try
            {
                var messages = new List<ChatMessage>
                {
                    new(ChatRole.System, IntentCheckPrompt),
                    new(ChatRole.User, request)
                };

                var response = await _chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
                var answer = response.Text?.Trim().ToLowerInvariant() ?? "";
                return answer.StartsWith("yes");
            }
            catch
            {
                // If intent check fails, allow the request through —
                // better to attempt planning than to block on a classifier error
                return true;
            }
        }

        private const string NotTravelRefusal =
            "I can only help with travel planning. Please describe a trip you'd like to plan, " +
            "including a destination and optionally a budget, duration, and travel style.";

        private static string? TryResolveAgent(string executorId)
        {
            foreach (var kvp in ExecutorMap)
            {
                if (executorId.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
                    return kvp.Value;
            }

            return null;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Multi-turn conversation surface
        // ─────────────────────────────────────────────────────────────────────────

        private const string RouterPrompt = """
            You are a router for a travel-planning multi-agent system. Given the conversation history
            and the new user message, decide which subset of agents needs to re-run.

            Routes:
            - full      : new destination/dates/scope, or any request that needs fresh research
            - replan    : itinerary tweaks within the same trip (swap days, change activities, reorder)
            - rebudget  : budget/cost adjustments only ("make it cheaper", "increase budget", "what if I add $200")
            - reaudit   : re-validate the existing plan ("any safety issues?", "is day 4 too packed?")
            - clarify   : Q&A about the existing plan, no plan change ("what's the visa story?", "what does this mean?")
            - offtopic  : not travel-related

            Respond with exactly one of: full, replan, rebudget, reaudit, clarify, offtopic
            No explanation. No punctuation.
            """;

        /// <inheritdoc />
        public async Task<TurnRoute> RouteAsync(
            IReadOnlyList<ChatMessage> history,
            string newMessage,
            CancellationToken cancellationToken = default)
        {
            var (route, _) = await RouteWithUsageAsync(history, newMessage, cancellationToken);
            return route;
        }

        /// <summary>
        /// Same as <see cref="RouteAsync"/> but also returns the router's <see cref="UsageDetails"/>
        /// so the turn-completion log can record router token cost. Internal because the public
        /// interface should stay narrow; turn-level callers use this overload.
        /// </summary>
        private async Task<(TurnRoute Route, UsageDetails? Usage)> RouteWithUsageAsync(
            IReadOnlyList<ChatMessage> history,
            string newMessage,
            CancellationToken cancellationToken)
        {
            try
            {
                // Compact prior turns to keep router cost predictable — we only need enough
                // context for the model to tell "is this a tweak or a new trip?"
                var transcript = new StringBuilder();
                foreach (var msg in history.TakeLast(8))
                {
                    var role = msg.Role == ChatRole.User ? "User" : "Assistant";
                    var text = msg.Text ?? string.Empty;
                    if (text.Length > 400) text = text[..400] + "…";
                    transcript.Append(role).Append(": ").AppendLine(text);
                }
                transcript.Append("User: ").Append(newMessage);

                var messages = new List<ChatMessage>
                {
                    new(ChatRole.System, RouterPrompt),
                    new(ChatRole.User, transcript.ToString())
                };

                var response = await _chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
                var token = (response.Text ?? string.Empty).Trim().ToLowerInvariant();

                var parsed = token switch
                {
                    "full" => TurnRoute.Full,
                    "replan" => TurnRoute.Replan,
                    "rebudget" => TurnRoute.Rebudget,
                    "reaudit" => TurnRoute.Reaudit,
                    "clarify" => TurnRoute.Clarify,
                    "offtopic" => TurnRoute.OffTopic,
                    _ => TurnRoute.Full // defensive: prefer doing more work over silently dropping intent
                };
                return (parsed, response.Usage);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Router LLM call failed; defaulting to TurnRoute.Full");
                return (TurnRoute.Full, null);
            }
        }

        /// <inheritdoc />
        public async Task<ConversationTurnResponse> ContinueConversationAsync(
            Conversation conversation,
            string newMessage,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversation);
            ArgumentException.ThrowIfNullOrWhiteSpace(newMessage);

            var stopwatch = Stopwatch.StartNew();

            // Route decision (first turn skips the router)
            TurnRoute route;
            UsageDetails? routerUsage;
            if (conversation.History.Count == 0)
            {
                route = TurnRoute.Full;
                routerUsage = null;
            }
            else
            {
                (route, routerUsage) = await RouteWithUsageAsync(conversation.History, newMessage, cancellationToken);
            }

            if (route == TurnRoute.OffTopic)
            {
                conversation.History.Add(new ChatMessage(ChatRole.User, newMessage));
                conversation.History.Add(new ChatMessage(ChatRole.Assistant, NotTravelRefusal));
                UpdateConversationMetadata(conversation, newMessage);
                stopwatch.Stop();

                LogTurnCompleted(conversation.Id, TurnRoute.OffTopic, Array.Empty<string>(),
                    stopwatch.ElapsedMilliseconds, success: true, error: null, routerUsage);

                return new ConversationTurnResponse
                {
                    Success = true,
                    Route = TurnRoute.OffTopic,
                    TravelPlan = conversation.LatestPlan ?? string.Empty,
                    AssistantReply = NotTravelRefusal,
                    ProcessingTime = stopwatch.Elapsed,
                    AgentsUsed = 0
                };
            }

            var agents = SelectAgents(route);
            var workflow = AgentWorkflowBuilder.BuildSequential(agents);
            var input = BuildWorkflowInput(conversation, newMessage);

            var aggregatorBuffer = new StringBuilder();
            string? agentError = null;
            var completedAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? currentAgent = null;

            StreamingRun run = await InProcessExecution.StreamAsync(workflow, input);
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

            await foreach (var evt in run.WatchStreamAsync().WithCancellation(cancellationToken))
            {
                if (evt is ExecutorInvokedEvent invokedEvt)
                {
                    currentAgent = TryResolveAgent(invokedEvt.ExecutorId);
                }
                else if (evt is AgentRunUpdateEvent e)
                {
                    var content = e.Data?.ToString();
                    if (string.IsNullOrEmpty(content)) continue;
                    if (string.Equals(currentAgent, "Aggregator", StringComparison.OrdinalIgnoreCase))
                        aggregatorBuffer.Append(content);
                }
                else if (evt is ExecutorCompletedEvent completedEvt)
                {
                    var resolved = TryResolveAgent(completedEvt.ExecutorId);
                    if (resolved != null) completedAgents.Add(resolved);
                }
                else if (evt is ExecutorFailedEvent failedEvt)
                {
                    var ex = failedEvt.Data as Exception;
                    agentError = $"Agent {failedEvt.ExecutorId} failed: {ex?.InnerException?.Message ?? ex?.Message ?? "unknown error"}";
                }
                else if (evt is WorkflowErrorEvent errorEvt)
                {
                    var ex = errorEvt.Data as Exception;
                    agentError = $"Workflow error: {ex?.InnerException?.Message ?? ex?.Message ?? "unknown error"}";
                    break;
                }
                else if (evt is WorkflowOutputEvent)
                {
                    break;
                }
            }

            stopwatch.Stop();

            var finalText = aggregatorBuffer.ToString();
            var success = agentError == null && !string.IsNullOrWhiteSpace(finalText);

            if (success)
            {
                conversation.History.Add(new ChatMessage(ChatRole.User, newMessage));
                conversation.History.Add(new ChatMessage(ChatRole.Assistant, finalText));
                UpdateConversationMetadata(conversation, newMessage);

                if (route != TurnRoute.Clarify)
                    conversation.LatestPlan = finalText;
            }

            LogTurnCompleted(conversation.Id, route, completedAgents.ToArray(),
                stopwatch.ElapsedMilliseconds, success, agentError, routerUsage);

            return new ConversationTurnResponse
            {
                Success = success,
                Route = route,
                TravelPlan = route == TurnRoute.Clarify
                    ? (conversation.LatestPlan ?? string.Empty)
                    : (success ? finalText : string.Empty),
                AssistantReply = route == TurnRoute.Clarify && success ? finalText : null,
                Error = agentError,
                ProcessingTime = stopwatch.Elapsed,
                AgentsUsed = completedAgents.Count
            };
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<TravelPlanProgress> ContinueConversationStreamingAsync(
            Conversation conversation,
            string newMessage,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conversation);
            ArgumentException.ThrowIfNullOrWhiteSpace(newMessage);

            var turnStopwatch = Stopwatch.StartNew();

            // 1. Route
            TurnRoute route;
            UsageDetails? routerUsage;
            if (conversation.History.Count == 0)
            {
                route = TurnRoute.Full;
                routerUsage = null;
            }
            else
            {
                (route, routerUsage) = await RouteWithUsageAsync(conversation.History, newMessage, cancellationToken);
            }

            yield return new TravelPlanProgress
            {
                CurrentAgent = "system",
                Status = ProgressStatus.Routed,
                Route = route,
                ProgressPercent = 0
            };

            // 2. Off-topic short-circuit
            if (route == TurnRoute.OffTopic)
            {
                conversation.History.Add(new ChatMessage(ChatRole.User, newMessage));
                conversation.History.Add(new ChatMessage(ChatRole.Assistant, NotTravelRefusal));
                UpdateConversationMetadata(conversation, newMessage);

                yield return new TravelPlanProgress
                {
                    CurrentAgent = "system",
                    Status = ProgressStatus.Clarified,
                    PartialOutput = NotTravelRefusal,
                    Route = TurnRoute.OffTopic,
                    ProgressPercent = 100
                };

                turnStopwatch.Stop();
                LogTurnCompleted(conversation.Id, TurnRoute.OffTopic, Array.Empty<string>(),
                    turnStopwatch.ElapsedMilliseconds, success: true, error: null, routerUsage);

                yield return new TravelPlanProgress
                {
                    CurrentAgent = "Complete",
                    Status = ProgressStatus.Completed,
                    ProgressPercent = 100
                };
                yield break;
            }

            // 3. Build workflow over the selected subset
            var agents = SelectAgents(route);
            var workflow = AgentWorkflowBuilder.BuildSequential(agents);
            var input = BuildWorkflowInput(conversation, newMessage);
            var subsetNames = AgentNamesFor(route);
            var completedAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            StreamingRun run = await InProcessExecution.StreamAsync(workflow, input);
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

            var aggregatorBuffer = new StringBuilder();
            string currentAgent = subsetNames[0];
            string? agentError = null;

            await foreach (var evt in run.WatchStreamAsync().WithCancellation(cancellationToken))
            {
                if (evt is ExecutorInvokedEvent invokedEvt)
                {
                    var resolved = TryResolveAgent(invokedEvt.ExecutorId);
                    if (resolved == null) continue;
                    currentAgent = resolved;

                    yield return new TravelPlanProgress
                    {
                        CurrentAgent = currentAgent,
                        Status = ProgressStatus.Starting,
                        Route = route,
                        ProgressPercent = ComputeSubsetProgress(subsetNames, currentAgent, started: true)
                    };
                }
                else if (evt is AgentRunUpdateEvent e)
                {
                    var content = e.Data?.ToString();
                    if (string.IsNullOrEmpty(content)) continue;

                    if (string.Equals(currentAgent, "Aggregator", StringComparison.OrdinalIgnoreCase))
                        aggregatorBuffer.Append(content);

                    yield return new TravelPlanProgress
                    {
                        CurrentAgent = currentAgent,
                        Status = ProgressStatus.Processing,
                        PartialOutput = content,
                        Route = route,
                        ProgressPercent = ComputeSubsetProgress(subsetNames, currentAgent, started: true)
                    };
                }
                else if (evt is ExecutorCompletedEvent completedEvt)
                {
                    var resolved = TryResolveAgent(completedEvt.ExecutorId);
                    if (resolved == null) continue;
                    completedAgents.Add(resolved);

                    yield return new TravelPlanProgress
                    {
                        CurrentAgent = resolved,
                        Status = ProgressStatus.Completed,
                        Route = route,
                        ProgressPercent = ComputeSubsetProgress(subsetNames, resolved, started: false)
                    };
                }
                else if (evt is ExecutorFailedEvent failedEvt)
                {
                    var resolved = TryResolveAgent(failedEvt.ExecutorId);
                    if (resolved == null) continue;
                    var ex = failedEvt.Data as Exception;
                    agentError = ex?.InnerException?.Message ?? ex?.Message ?? "Agent failed";

                    yield return new TravelPlanProgress
                    {
                        CurrentAgent = resolved,
                        Status = ProgressStatus.Error,
                        PartialOutput = agentError,
                        Route = route,
                        ProgressPercent = 0
                    };
                }
                else if (evt is WorkflowErrorEvent errorEvt)
                {
                    var ex = errorEvt.Data as Exception;
                    agentError = ex?.InnerException?.Message ?? ex?.Message ?? "Workflow error";

                    yield return new TravelPlanProgress
                    {
                        CurrentAgent = currentAgent,
                        Status = ProgressStatus.Error,
                        PartialOutput = agentError,
                        Route = route,
                        ProgressPercent = 0
                    };
                    break;
                }
                else if (evt is WorkflowOutputEvent)
                {
                    break;
                }
            }

            // 4. Commit only on success — leaves conversation untouched on error/cancel
            if (agentError != null)
            {
                turnStopwatch.Stop();
                LogTurnCompleted(conversation.Id, route, completedAgents.ToArray(),
                    turnStopwatch.ElapsedMilliseconds, success: false, agentError, routerUsage);
                yield break;
            }

            var finalText = aggregatorBuffer.ToString();
            if (string.IsNullOrWhiteSpace(finalText))
            {
                yield return new TravelPlanProgress
                {
                    CurrentAgent = "system",
                    Status = ProgressStatus.Error,
                    PartialOutput = "Aggregator produced no output",
                    Route = route,
                    ProgressPercent = 0
                };
                turnStopwatch.Stop();
                LogTurnCompleted(conversation.Id, route, completedAgents.ToArray(),
                    turnStopwatch.ElapsedMilliseconds, success: false, "Aggregator produced no output", routerUsage);
                yield break;
            }

            conversation.History.Add(new ChatMessage(ChatRole.User, newMessage));
            conversation.History.Add(new ChatMessage(ChatRole.Assistant, finalText));
            UpdateConversationMetadata(conversation, newMessage);

            if (route == TurnRoute.Clarify)
            {
                yield return new TravelPlanProgress
                {
                    CurrentAgent = "system",
                    Status = ProgressStatus.Clarified,
                    PartialOutput = finalText,
                    Route = route,
                    ProgressPercent = 100
                };
            }
            else
            {
                conversation.LatestPlan = finalText;
                yield return new TravelPlanProgress
                {
                    CurrentAgent = "system",
                    Status = ProgressStatus.PlanFinal,
                    PartialOutput = finalText,
                    Route = route,
                    ProgressPercent = 100
                };
            }

            turnStopwatch.Stop();
            LogTurnCompleted(conversation.Id, route, completedAgents.ToArray(),
                turnStopwatch.ElapsedMilliseconds, success: true, error: null, routerUsage);

            yield return new TravelPlanProgress
            {
                CurrentAgent = "Complete",
                Status = ProgressStatus.Completed,
                Route = route,
                ProgressPercent = 100
            };
        }

        /// <summary>
        /// Single Serilog structured-log emit point for completed turns. Fields land as
        /// individual properties in Serilog sinks (console + rolling file) and are queryable
        /// without parsing the message text. See <c>TurnUsage.cs</c> for the planned table
        /// shape that will persist this data once the migrations framework lands.
        /// </summary>
        private void LogTurnCompleted(
            string conversationId,
            TurnRoute route,
            IReadOnlyList<string> agentsRun,
            long durationMs,
            bool success,
            string? error,
            UsageDetails? routerUsage)
        {
            _logger.LogInformation(
                "Conversation turn completed: ConversationId={ConversationId} Route={Route} " +
                "AgentsRun={AgentsRun} DurationMs={DurationMs} Success={Success} " +
                "RouterInputTokens={RouterInputTokens} RouterOutputTokens={RouterOutputTokens} " +
                "Error={Error}",
                conversationId,
                route.ToString(),
                string.Join(",", agentsRun),
                durationMs,
                success,
                routerUsage?.InputTokenCount,
                routerUsage?.OutputTokenCount,
                error);
        }

        private List<ChatClientAgent> SelectAgents(TurnRoute route) => route switch
        {
            TurnRoute.Full     => new() { _researcher, _planner, _accountant, _auditor, _aggregator },
            TurnRoute.Replan   => new() { _planner, _accountant, _auditor, _aggregator },
            TurnRoute.Rebudget => new() { _accountant, _auditor, _aggregator },
            TurnRoute.Reaudit  => new() { _auditor, _aggregator },
            TurnRoute.Clarify  => new() { _aggregator },
            _ => throw new InvalidOperationException($"No agent subset for route {route}")
        };

        private static readonly Dictionary<TurnRoute, string[]> RouteAgentNames = new()
        {
            [TurnRoute.Full]     = new[] { "Researcher", "Planner", "Accountant", "Auditor", "Aggregator" },
            [TurnRoute.Replan]   = new[] { "Planner", "Accountant", "Auditor", "Aggregator" },
            [TurnRoute.Rebudget] = new[] { "Accountant", "Auditor", "Aggregator" },
            [TurnRoute.Reaudit]  = new[] { "Auditor", "Aggregator" },
            [TurnRoute.Clarify]  = new[] { "Aggregator" },
        };

        /// <summary>Public for the controller's SSE <c>route</c> event payload.</summary>
        public static IReadOnlyList<string> AgentNamesFor(TurnRoute route)
            => RouteAgentNames.TryGetValue(route, out var names) ? names : Array.Empty<string>();

        private static List<ChatMessage> BuildWorkflowInput(Conversation conv, string newMessage)
        {
            // The MAF agent workflow operates on List<ChatMessage>. Passing the full conversation
            // history lets each agent see prior turns naturally. For subset routes (Replan, Rebudget,
            // Reaudit, Clarify), the prior aggregator output is already in History as an assistant
            // message — that's enough anchor context for downstream agents.
            var input = new List<ChatMessage>(conv.History.Count + 1);
            input.AddRange(conv.History);
            input.Add(new ChatMessage(ChatRole.User, newMessage));
            return input;
        }

        private static int ComputeSubsetProgress(IReadOnlyList<string> subset, string agent, bool started)
        {
            var idx = -1;
            for (var i = 0; i < subset.Count; i++)
                if (string.Equals(subset[i], agent, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
            if (idx < 0) return 0;
            var step = started ? idx : idx + 1;
            return (step * 100) / subset.Count;
        }

        private static void UpdateConversationMetadata(Conversation conv, string newMessage)
        {
            conv.LastActivity = DateTime.UtcNow;
            if (string.IsNullOrEmpty(conv.Title))
            {
                var trimmed = newMessage.Trim();
                conv.Title = trimmed.Length <= 60 ? trimmed : trimmed[..60] + "…";
            }
        }
    }
}
