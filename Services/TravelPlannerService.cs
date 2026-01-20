using System.Diagnostics;
using System.Runtime.CompilerServices;
using LocalAgentTravelPlanner.Agents;
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
        public TravelPlannerService(IChatClient chatClient)
        {
            _chatClient = chatClient;

            // Initialize all agents using the factory pattern
            // Factories encapsulate agent configuration (prompts, tools)
            _researcher = ResearcherAgentFactory.Create(_chatClient);
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
                // Build the sequential workflow
                var workflow = AgentWorkflowBuilder.BuildSequential(
                    new List<ChatClientAgent> { _researcher, _planner, _accountant, _auditor, _aggregator }
                );

                // Execute and collect all output
                var outputBuilder = new System.Text.StringBuilder();

                StreamingRun run = await InProcessExecution.StreamAsync(workflow, request);
                await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

                await foreach (WorkflowEvent evt in run.WatchStreamAsync().WithCancellation(cancellationToken))
                {
                    if (evt is AgentRunUpdateEvent e)
                    {
                        outputBuilder.Append(e.Data);
                    }
                    else if (evt is WorkflowOutputEvent)
                    {
                        break;
                    }
                }

                stopwatch.Stop();

                return new TravelPlanResponse
                {
                    Success = true,
                    TravelPlan = outputBuilder.ToString(),
                    ProcessingTime = stopwatch.Elapsed,
                    AgentsUsed = 5 // Researcher, Planner, Accountant, Auditor, Aggregator
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
            var agentNames = new[] { "Researcher", "Planner", "Accountant", "Auditor", "Aggregator" };
            var currentAgentIndex = 0;

            // Build the sequential workflow
            var workflow = AgentWorkflowBuilder.BuildSequential(
                new List<ChatClientAgent> { _researcher, _planner, _accountant, _auditor, _aggregator }
            );

            yield return new TravelPlanProgress
            {
                CurrentAgent = agentNames[currentAgentIndex],
                Status = ProgressStatus.Starting,
                ProgressPercent = 0
            };

            StreamingRun run = await InProcessExecution.StreamAsync(workflow, request);
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

            var partialOutput = new System.Text.StringBuilder();

            await foreach (WorkflowEvent evt in run.WatchStreamAsync().WithCancellation(cancellationToken))
            {
                if (evt is AgentRunUpdateEvent e)
                {
                    partialOutput.Append(e.Data);

                    yield return new TravelPlanProgress
                    {
                        CurrentAgent = agentNames[Math.Min(currentAgentIndex, agentNames.Length - 1)],
                        Status = ProgressStatus.Processing,
                        PartialOutput = e.Data?.ToString(),
                        ProgressPercent = (currentAgentIndex * 100) / agentNames.Length
                    };
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
    }
}
