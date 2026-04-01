using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LocalAgentTravelPlanner.Agents;
using LocalAgentTravelPlanner.Api.Models;
using LocalAgentTravelPlanner.Services;
using LocalAgentTravelPlanner.Tools;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.AI;

namespace LocalAgentTravelPlanner.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TravelController : ControllerBase
{
    private readonly ILogger<TravelController> _logger;
    private readonly ResearchTools _researchTools;
    private readonly TravelTools _travelTools;

    public TravelController(
        ILogger<TravelController> logger,
        ResearchTools researchTools,
        TravelTools travelTools)
    {
        _logger = logger;
        _researchTools = researchTools;
        _travelTools = travelTools;
    }

    /// <summary>
    /// Creates a travel plan based on the provided request.
    /// This endpoint processes the request through all 5 agents sequentially.
    /// </summary>
    [HttpPost("plan")]
    [EnableRateLimiting("plan")]
    [ProducesResponseType(typeof(TravelPlanApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<TravelPlanApiResponse>> CreatePlan(
        [FromBody] TravelPlanRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("Received travel plan request: {Request}", request.Request);

            // Create chat client based on provider preference
            var (chatClient, provider, model) = ChatClientFactory.CreateWithAutoDetect(request.Provider);

            _logger.LogInformation("Using provider: {Provider}, model: {Model}", provider, model);

            // Create agents
            var researcher = ResearcherAgentFactory.Create(chatClient, _researchTools, _travelTools);
            var planner = PlannerAgentFactory.Create(chatClient);
            var accountant = AccountantAgentFactory.Create(chatClient);
            var auditor = AuditorAgentFactory.Create(chatClient);
            var aggregator = AggregatorAgentFactory.Create(chatClient);

            // Build sequential workflow
            var workflow = AgentWorkflowBuilder.BuildSequential(
                new List<ChatClientAgent> { researcher, planner, accountant, auditor, aggregator }
            );

            // Execute workflow and collect output
            var outputBuilder = new System.Text.StringBuilder();
            StreamingRun run = await InProcessExecution.StreamAsync(workflow, request.Request);
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

            string? agentError = null;

            await foreach (var evt in run.WatchStreamAsync().WithCancellation(cancellationToken))
            {
                if (evt is AgentRunUpdateEvent updateEvent)
                {
                    outputBuilder.Append(updateEvent.Data);
                }
                else if (evt is ExecutorFailedEvent failedEvt)
                {
                    var ex = failedEvt.Data as Exception;
                    agentError = $"Agent {failedEvt.ExecutorId} failed: {ex?.InnerException?.Message ?? ex?.Message ?? "unknown error"}";
                    _logger.LogError(ex, "Agent failed: {ExecutorId}", failedEvt.ExecutorId);
                }
                else if (evt is WorkflowErrorEvent errorEvt)
                {
                    var ex = errorEvt.Data as Exception;
                    agentError = $"Workflow error: {ex?.InnerException?.Message ?? ex?.Message ?? "unknown error"}";
                    _logger.LogError(ex, "Workflow error");
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

            var response = new TravelPlanApiResponse
            {
                Success = success,
                TravelPlan = output,
                Error = agentError,
                ProcessingTimeSeconds = stopwatch.Elapsed.TotalSeconds,
                Provider = provider.ToString(),
                Model = model
            };

            return success
                ? Ok(response)
                : StatusCode(StatusCodes.Status500InternalServerError, response);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("ANTHROPIC_API_KEY"))
        {
            _logger.LogWarning("Anthropic API key not configured: {Message}", ex.Message);
            return BadRequest(new TravelPlanApiResponse
            {
                Success = false,
                TravelPlan = string.Empty,
                Error = "Anthropic API key not configured. Set ANTHROPIC_API_KEY environment variable or use provider=ollama.",
                Provider = "none",
                Model = "none"
            });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to LLM provider");
            return StatusCode(503, new TravelPlanApiResponse
            {
                Success = false,
                TravelPlan = string.Empty,
                Error = $"Failed to connect to LLM provider: {ex.Message}",
                Provider = request.Provider ?? "auto",
                Model = "unknown"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating travel plan");
            return StatusCode(500, new TravelPlanApiResponse
            {
                Success = false,
                TravelPlan = string.Empty,
                Error = ex.Message,
                Provider = request.Provider ?? "auto",
                Model = "unknown"
            });
        }
    }

    /// <summary>
    /// Creates a travel plan with Server-Sent Events for real-time streaming.
    /// Connect to this endpoint using EventSource in JavaScript.
    /// </summary>
    [HttpGet("plan/stream")]
    [EnableRateLimiting("plan")]
    [Produces("text/event-stream")]
    public async Task StreamPlan(
        [FromQuery] string? request,
        [FromQuery] string? provider,
        CancellationToken cancellationToken)
    {
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");
        Response.ContentType = "text/event-stream";

        // Validate query params — [FromQuery] doesn't participate in model validation
        if (string.IsNullOrWhiteSpace(request) || request.Length < 10)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await SendSseEvent("error", new TravelPlanProgressEvent
            {
                Agent = "system",
                Status = "error",
                Content = "Request must be at least 10 characters.",
                ProgressPercent = 0
            });
            return;
        }

        if (request.Length > 2000)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await SendSseEvent("error", new TravelPlanProgressEvent
            {
                Agent = "system",
                Status = "error",
                Content = "Request must not exceed 2000 characters.",
                ProgressPercent = 0
            });
            return;
        }

        try
        {
            _logger.LogInformation("Received streaming travel plan request: {Request}", request);

            // Create chat client based on provider preference
            var (chatClient, providerUsed, model) = ChatClientFactory.CreateWithAutoDetect(provider);

            // Send initial event
            await SendSseEvent("init", new TravelPlanProgressEvent
            {
                Agent = "system",
                Status = "starting",
                Content = $"Using {providerUsed} with model {model}",
                ProgressPercent = 0
            });

            // Create agents
            var researcher = ResearcherAgentFactory.Create(chatClient, _researchTools, _travelTools);
            var planner = PlannerAgentFactory.Create(chatClient);
            var accountant = AccountantAgentFactory.Create(chatClient);
            var auditor = AuditorAgentFactory.Create(chatClient);
            var aggregator = AggregatorAgentFactory.Create(chatClient);

            // Build sequential workflow
            var workflow = AgentWorkflowBuilder.BuildSequential(
                new List<ChatClientAgent> { researcher, planner, accountant, auditor, aggregator }
            );

            // Execute workflow with streaming
            StreamingRun run = await InProcessExecution.StreamAsync(workflow, request);
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

            string currentAgent = "researcher";
            // Map ExecutorId prefixes to UI agent names
            var executorMap = new Dictionary<string, (string Name, int Index)>
            {
                ["Researcher_Agent"] = ("researcher", 0),
                ["Planner_Agent"] = ("planner", 1),
                ["Accountant_Agent"] = ("accountant", 2),
                ["Auditor_Agent"] = ("auditor", 3),
                ["Aggregator_Agent"] = ("aggregator", 4),
            };

            // Returns null for unknown executor IDs (e.g. internal tool executors)
            string? TryResolveAgent(string executorId)
            {
                foreach (var kvp in executorMap)
                {
                    if (executorId.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
                        return kvp.Value.Name;
                }
                return null;
            }

            int GetAgentIndex(string agentName) =>
                executorMap.Values.FirstOrDefault(v => v.Name == agentName).Index;

            await foreach (var evt in run.WatchStreamAsync().WithCancellation(cancellationToken))
            {
                if (evt is ExecutorInvokedEvent invokedEvt)
                {
                    var resolved = TryResolveAgent(invokedEvt.ExecutorId);
                    if (resolved == null) continue; // Skip internal/tool executors
                    currentAgent = resolved;
                    var idx = GetAgentIndex(resolved);

                    await SendSseEvent("agent-start", new TravelPlanProgressEvent
                    {
                        Agent = currentAgent,
                        Status = "started",
                        ProgressPercent = idx * 20
                    });
                }
                else if (evt is AgentRunUpdateEvent updateEvent)
                {
                    var idx = GetAgentIndex(currentAgent);
                    await SendSseEvent("content", new TravelPlanProgressEvent
                    {
                        Agent = currentAgent,
                        Status = "processing",
                        Content = updateEvent.Data?.ToString(),
                        ProgressPercent = (idx * 20) + 10
                    });
                }
                else if (evt is ExecutorCompletedEvent completedEvt)
                {
                    var resolved = TryResolveAgent(completedEvt.ExecutorId);
                    if (resolved == null) continue; // Skip internal/tool executors
                    var idx = GetAgentIndex(resolved);
                    await SendSseEvent("agent-complete", new TravelPlanProgressEvent
                    {
                        Agent = resolved,
                        Status = "completed",
                        ProgressPercent = (idx + 1) * 20
                    });
                }
                else if (evt is ExecutorFailedEvent failedEvt)
                {
                    var resolved = TryResolveAgent(failedEvt.ExecutorId);
                    if (resolved == null) continue; // Skip internal/tool executors
                    var ex = failedEvt.Data as Exception;
                    await SendSseEvent("error", new TravelPlanProgressEvent
                    {
                        Agent = resolved,
                        Status = "failed",
                        Content = ex?.InnerException?.Message ?? ex?.Message ?? "Agent failed",
                        ProgressPercent = GetAgentIndex(resolved) * 20
                    });
                }
                else if (evt is WorkflowErrorEvent errorEvt)
                {
                    var ex = errorEvt.Data as Exception;
                    await SendSseEvent("error", new TravelPlanProgressEvent
                    {
                        Agent = "system",
                        Status = "error",
                        Content = ex?.InnerException?.Message ?? ex?.Message ?? "Workflow error",
                        ProgressPercent = 0
                    });
                    break;
                }
                else if (evt is WorkflowOutputEvent)
                {
                    break;
                }
            }

            // Send completion event
            await SendSseEvent("complete", new TravelPlanProgressEvent
            {
                Agent = "system",
                Status = "completed",
                ProgressPercent = 100
            });
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Streaming request was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during streaming");
            await SendSseEvent("error", new TravelPlanProgressEvent
            {
                Agent = "system",
                Status = "error",
                Content = ex.Message,
                ProgressPercent = 0
            });
        }
    }

    private async Task SendSseEvent(string eventType, TravelPlanProgressEvent data)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        await Response.WriteAsync($"event: {eventType}\n");
        await Response.WriteAsync($"data: {json}\n\n");
        await Response.Body.FlushAsync();
    }

    /// <summary>
    /// Health check endpoint to verify the API is running.
    /// </summary>
    [HttpGet("health")]
    public ActionResult<object> Health()
    {
        // Check which providers are available
        var anthropicAvailable = !string.IsNullOrEmpty(
            Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

        return Ok(new
        {
            Status = "healthy",
            Timestamp = DateTime.UtcNow,
            Providers = new
            {
                Ollama = "configured (local server required)",
                Anthropic = anthropicAvailable ? "available" : "not configured"
            }
        });
    }
}
