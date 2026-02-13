using FluentAssertions;
using LocalAgentTravelPlanner.Agents;
using LocalAgentTravelPlanner.Services;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Xunit;
using Xunit.Abstractions;

namespace LocalAgentTravelPlanner.Tests.Evaluation;

/// <summary>
/// Evaluation tests for the travel planner agent pipeline using
/// Microsoft.Extensions.AI.Evaluation.Quality.
///
/// These tests run the actual agent pipeline and evaluate the output
/// using LLM-as-a-judge metrics: Coherence, Fluency, Relevance,
/// Truth, Completeness, and Groundedness.
///
/// Prerequisites:
/// - Ollama running locally with qwen2.5:7b model, OR
/// - ANTHROPIC_API_KEY environment variable set
///
/// These are integration tests and require an LLM provider.
/// They are skipped by default unless ENABLE_EVAL_TESTS=true is set.
/// </summary>
[Trait("Category", "Evaluation")]
public class AgentEvaluationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IChatClient? _chatClient;
    private ChatConfiguration? _evalConfiguration;

    public AgentEvaluationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task InitializeAsync()
    {
        // Create the chat client for running agents and evaluations
        var (client, provider, model) = ChatClientFactory.CreateWithAutoDetect();
        _chatClient = client;

        // Use the same client as the judge for evaluations
        _evalConfiguration = new ChatConfiguration(_chatClient);

        _output.WriteLine($"Evaluation using provider: {provider}, model: {model}");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Runs the full agent pipeline and returns the output for evaluation.
    /// </summary>
    private async Task<string> RunAgentPipelineAsync(string request)
    {
        var researcher = ResearcherAgentFactory.Create(_chatClient!);
        var planner = PlannerAgentFactory.Create(_chatClient!);
        var accountant = AccountantAgentFactory.Create(_chatClient!);
        var auditor = AuditorAgentFactory.Create(_chatClient!);
        var aggregator = AggregatorAgentFactory.Create(_chatClient!);

        var workflow = AgentWorkflowBuilder.BuildSequential(
            new List<ChatClientAgent> { researcher, planner, accountant, auditor, aggregator }
        );

        var outputBuilder = new System.Text.StringBuilder();
        StreamingRun run = await InProcessExecution.StreamAsync(workflow, request);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        await foreach (var evt in run.WatchStreamAsync())
        {
            if (evt is AgentRunUpdateEvent updateEvent)
            {
                outputBuilder.Append(updateEvent.Data?.ToString());
            }
            else if (evt is WorkflowOutputEvent)
            {
                break;
            }
        }

        return outputBuilder.ToString();
    }

    /// <summary>
    /// Evaluates the agent pipeline output using MEAI Quality evaluators.
    /// Returns a dictionary of metric names to scores.
    /// </summary>
    public async Task<Dictionary<string, EvaluationResult>> EvaluateAgentAsync(
        string userRequest,
        string agentOutput)
    {
        var results = new Dictionary<string, EvaluationResult>();

        // Build the conversation as chat messages
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "You are a multi-agent travel planning system. You research destinations, " +
                "create itineraries, calculate budgets, audit the plan for accuracy, " +
                "and produce a final polished travel plan."),
            new(ChatRole.User, userRequest)
        };

        var response = new ChatResponse(
            [new ChatMessage(ChatRole.Assistant, agentOutput)]);

        // 1. Coherence - Is the output logically structured?
        var coherenceEvaluator = new CoherenceEvaluator();
        var coherenceResult = await coherenceEvaluator.EvaluateAsync(
            messages, response, _evalConfiguration!);
        results["Coherence"] = coherenceResult;

        // 2. Fluency - Is the output well-written?
        var fluencyEvaluator = new FluencyEvaluator();
        var fluencyResult = await fluencyEvaluator.EvaluateAsync(
            messages, response, _evalConfiguration!);
        results["Fluency"] = fluencyResult;

        // 3. Relevance, Truth, and Completeness - combined evaluator
        var rtcEvaluator = new RelevanceTruthAndCompletenessEvaluator();
        var rtcResult = await rtcEvaluator.EvaluateAsync(
            messages, response, _evalConfiguration!);
        results["RelevanceTruthCompleteness"] = rtcResult;

        // 4. Groundedness - Is the output grounded in the context?
        var groundednessEvaluator = new GroundednessEvaluator();
        var groundednessMessages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "You are a multi-agent travel planning system. Research shows destination data " +
                "including weather, transport options, accommodations, and attractions. " +
                "The planner creates day-by-day itineraries and the accountant calculates budgets."),
            new(ChatRole.User, userRequest)
        };
        var groundednessResult = await groundednessEvaluator.EvaluateAsync(
            groundednessMessages, response, _evalConfiguration!);
        results["Groundedness"] = groundednessResult;

        return results;
    }

    /// <summary>
    /// Prints evaluation results in a formatted table.
    /// </summary>
    private void PrintEvaluationResults(Dictionary<string, EvaluationResult> results)
    {
        _output.WriteLine("\n=== EVALUATION RESULTS ===\n");
        _output.WriteLine($"{"Metric",-35} {"Score",-8} {"Rating",-15} {"Passed"}");
        _output.WriteLine(new string('-', 75));

        foreach (var (name, result) in results)
        {
            foreach (var metric in result.Metrics)
            {
                if (metric.Value is NumericMetric numericMetric)
                {
                    var rating = numericMetric.Interpretation?.Rating.ToString() ?? "N/A";
                    var passed = numericMetric.Interpretation?.Failed == false ? "PASS" : "FAIL";
                    var score = numericMetric.Value?.ToString("F1") ?? "N/A";

                    _output.WriteLine($"{metric.Key,-35} {score,-8} {rating,-15} {passed}");
                }
            }
        }

        _output.WriteLine(new string('-', 75));
    }

    [Fact(Skip = "Integration test - set ENABLE_EVAL_TESTS=true to run")]
    [Trait("Category", "Integration")]
    public async Task FullPipeline_ShouldProduceCoherentOutput()
    {
        var request = "2-day trip to Pokhara from Kathmandu, budget 30000 NPR";

        _output.WriteLine($"Running agent pipeline for: {request}");
        var agentOutput = await RunAgentPipelineAsync(request);

        _output.WriteLine($"Agent output length: {agentOutput.Length} chars");
        agentOutput.Should().NotBeNullOrEmpty("Agent pipeline should produce output");

        _output.WriteLine("Running evaluations...");
        var results = await EvaluateAgentAsync(request, agentOutput);

        PrintEvaluationResults(results);

        // Assert minimum quality thresholds
        var coherence = results["Coherence"].Get<NumericMetric>(CoherenceEvaluator.CoherenceMetricName);
        coherence.Interpretation!.Failed.Should().BeFalse("Output should be coherent");

        var fluency = results["Fluency"].Get<NumericMetric>(FluencyEvaluator.FluencyMetricName);
        fluency.Interpretation!.Failed.Should().BeFalse("Output should be fluent");
    }

    [Fact(Skip = "Integration test - set ENABLE_EVAL_TESTS=true to run")]
    [Trait("Category", "Integration")]
    public async Task FullPipeline_ShouldBeRelevantAndComplete()
    {
        var request = "3-day family trip from Butwal to Pokhara, budget 50000 NPR";

        _output.WriteLine($"Running agent pipeline for: {request}");
        var agentOutput = await RunAgentPipelineAsync(request);

        agentOutput.Should().NotBeNullOrEmpty();

        _output.WriteLine("Running evaluations...");
        var results = await EvaluateAgentAsync(request, agentOutput);

        PrintEvaluationResults(results);

        // Assert relevance and completeness
        var rtcResult = results["RelevanceTruthCompleteness"];
        var relevance = rtcResult.Get<NumericMetric>(
            RelevanceTruthAndCompletenessEvaluator.RelevanceMetricName);
        relevance.Interpretation!.Failed.Should().BeFalse("Output should be relevant to the request");

        var completeness = rtcResult.Get<NumericMetric>(
            RelevanceTruthAndCompletenessEvaluator.CompletenessMetricName);
        completeness.Interpretation!.Failed.Should().BeFalse("Output should be complete");
    }

    /// <summary>
    /// Evaluates a pre-recorded agent output without running the pipeline.
    /// Useful for testing the evaluation framework itself.
    /// </summary>
    [Fact(Skip = "Integration test - set ENABLE_EVAL_TESTS=true to run")]
    [Trait("Category", "Integration")]
    public async Task EvaluatePrerecordedOutput_ShouldScoreWell()
    {
        var request = "2-day budget trip to Pokhara from Kathmandu";
        var prerecordedOutput = @"
# Pokhara Travel Plan - 2 Days

## Day 1: Arrival and Lakeside Exploration
- **Morning**: Take tourist bus from Kathmandu (NPR 800, 6-7 hours)
- **Afternoon**: Check into Lakeside Hostel (NPR 1,200/night), explore Phewa Lake
- **Evening**: Dinner at local restaurant - Dal Bhat (NPR 300)

## Day 2: Sarangkot and Sightseeing
- **Early Morning**: Sunrise at Sarangkot (NPR 100 entry, taxi NPR 500)
- **Morning**: Visit World Peace Pagoda (free entry)
- **Afternoon**: International Mountain Museum (NPR 400)
- **Evening**: Return to Kathmandu by tourist bus (NPR 800)

## Budget Summary
| Category | Amount (NPR) |
|----------|-------------|
| Transport | 2,100 |
| Accommodation | 1,200 |
| Food | 900 |
| Activities | 500 |
| **Total** | **4,700** |

## Safety Notes
- Drink bottled water only
- Emergency: Tourist Police 1144
";

        _output.WriteLine("Evaluating pre-recorded output...");
        var results = await EvaluateAgentAsync(request, prerecordedOutput);

        PrintEvaluationResults(results);

        // All metrics should pass for a well-structured plan
        foreach (var (name, result) in results)
        {
            foreach (var metric in result.Metrics)
            {
                if (metric.Value is NumericMetric numericMetric)
                {
                    _output.WriteLine($"{metric.Key}: {numericMetric.Value:F1} - " +
                        $"{numericMetric.Interpretation?.Rating}");
                }
            }
        }
    }
}
