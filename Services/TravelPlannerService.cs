using System.Diagnostics;
using System.Runtime.CompilerServices;
using LocalAgentTravelPlanner.Agents;
using LocalAgentTravelPlanner.Tools;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

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
        /// </summary>
        public TravelPlannerService(IChatClient chatClient, ResearchTools researchTools, TravelTools travelTools)
        {
            _chatClient = chatClient;

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
    }
}
