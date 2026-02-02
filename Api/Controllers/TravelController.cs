using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LocalAgentTravelPlanner.Agents;
using LocalAgentTravelPlanner.Api.Models;
using LocalAgentTravelPlanner.Services;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;

namespace LocalAgentTravelPlanner.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TravelController : ControllerBase
{
    private readonly ILogger<TravelController> _logger;

    public TravelController(ILogger<TravelController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Creates a travel plan based on the provided request.
    /// This endpoint processes the request through all 5 agents sequentially.
    /// </summary>
    [HttpPost("plan")]
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
            var researcher = ResearcherAgentFactory.Create(chatClient);
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

            await foreach (var evt in run.WatchStreamAsync().WithCancellation(cancellationToken))
            {
                if (evt is AgentRunUpdateEvent updateEvent)
                {
                    outputBuilder.Append(updateEvent.Data);
                }
                else if (evt is WorkflowOutputEvent)
                {
                    break;
                }
            }

            stopwatch.Stop();

            return Ok(new TravelPlanApiResponse
            {
                Success = true,
                TravelPlan = outputBuilder.ToString(),
                ProcessingTimeSeconds = stopwatch.Elapsed.TotalSeconds,
                Provider = provider.ToString(),
                Model = model
            });
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
    [Produces("text/event-stream")]
    public async Task StreamPlan(
        [FromQuery] string request,
        [FromQuery] string? provider,
        CancellationToken cancellationToken)
    {
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");
        Response.ContentType = "text/event-stream";

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
            var researcher = ResearcherAgentFactory.Create(chatClient);
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
            int agentIndex = 0;
            string[] agents = { "researcher", "planner", "accountant", "auditor", "aggregator" };

            await foreach (var evt in run.WatchStreamAsync().WithCancellation(cancellationToken))
            {
                if (evt is AgentRunUpdateEvent updateEvent)
                {
                    await SendSseEvent("content", new TravelPlanProgressEvent
                    {
                        Agent = currentAgent,
                        Status = "processing",
                        Content = updateEvent.Data?.ToString(),
                        ProgressPercent = (agentIndex * 20) + 10
                    });
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
                Ollama = "available (requires local server)",
                Anthropic = anthropicAvailable ? "available" : "not configured"
            }
        });
    }
}
