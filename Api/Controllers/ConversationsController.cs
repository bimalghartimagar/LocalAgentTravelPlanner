using System.Diagnostics;
using System.Text.Json;
using LocalAgentTravelPlanner.Api.Models;
using LocalAgentTravelPlanner.Services;
using LocalAgentTravelPlanner.Services.Conversations;
using LocalAgentTravelPlanner.Tools;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.AI;

namespace LocalAgentTravelPlanner.Api.Controllers;

/// <summary>
/// Multi-turn conversation surface. Sits alongside <see cref="TravelController"/>; the
/// one-shot endpoints there are unchanged. The chat history + latest plan live in
/// <see cref="IConversationStore"/>; the LLM provider is still picked per request because
/// <c>IChatClient</c> is provider-scoped.
/// </summary>
[ApiController]
[Route("api/conversations")]
public class ConversationsController : ControllerBase
{
    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger<ConversationsController> _logger;
    private readonly ILogger<TravelPlannerService> _serviceLogger;
    private readonly IConversationStore _store;
    private readonly IConversationLock _locks;
    private readonly ResearchTools _researchTools;
    private readonly TravelTools _travelTools;

    public ConversationsController(
        ILogger<ConversationsController> logger,
        ILogger<TravelPlannerService> serviceLogger,
        IConversationStore store,
        IConversationLock locks,
        ResearchTools researchTools,
        TravelTools travelTools)
    {
        _logger = logger;
        _serviceLogger = serviceLogger;
        _store = store;
        _locks = locks;
        _researchTools = researchTools;
        _travelTools = travelTools;
    }

    [HttpPost]
    public async Task<ActionResult<CreateConversationResponse>> Create(CancellationToken cancellationToken)
    {
        var conv = await _store.CreateAsync(cancellationToken);
        return Ok(new CreateConversationResponse { Id = conv.Id, CreatedAt = conv.CreatedAt });
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ConversationSummaryResponse>>> List(CancellationToken cancellationToken)
    {
        var summaries = await _store.ListAsync(cancellationToken);
        return Ok(summaries
            .Select(s => new ConversationSummaryResponse { Id = s.Id, Title = s.Title, LastActivity = s.LastActivity })
            .ToList());
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ConversationDetailResponse>> Get(string id, CancellationToken cancellationToken)
    {
        var conv = await _store.GetAsync(id, cancellationToken);
        if (conv == null) return NotFound();

        return Ok(new ConversationDetailResponse
        {
            Id = conv.Id,
            Title = conv.Title,
            History = conv.History.Select(m => new ConversationMessageDto
            {
                Role = m.Role == ChatRole.Assistant ? "assistant" : "user",
                Content = m.Text ?? string.Empty
            }).ToList(),
            LatestPlan = conv.LatestPlan,
            CreatedAt = conv.CreatedAt,
            LastActivity = conv.LastActivity
        });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
        => await _store.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();

    [HttpPost("{id}/messages")]
    [EnableRateLimiting("plan")]
    public async Task<ActionResult<ConversationTurnApiResponse>> SendMessage(
        string id,
        [FromBody] ConversationMessageRequest request,
        CancellationToken cancellationToken)
    {
        IAsyncDisposable? lockHandle;
        try
        {
            lockHandle = await _locks.AcquireAsync(id, cancellationToken);
        }
        catch (ConversationBusyException)
        {
            return Conflict(new { error = "Conversation is currently processing another message. Wait for it to finish and try again." });
        }

        await using (lockHandle)
        {
            var conv = await _store.GetAsync(id, cancellationToken);
            if (conv == null) return NotFound();

            var stopwatch = Stopwatch.StartNew();

            try
            {
                var (chatClient, provider, model) = ChatClientFactory.CreateWithAutoDetect(request.Provider);
                var service = BuildService(chatClient);

                var result = await service.ContinueConversationAsync(conv, request.Message, cancellationToken);
                await _store.UpdateAsync(conv, cancellationToken);
                stopwatch.Stop();

                return Ok(new ConversationTurnApiResponse
                {
                    Success = result.Success,
                    Route = result.Route.ToString().ToLowerInvariant(),
                    AgentsRun = TravelPlannerService.AgentNamesFor(result.Route).Select(n => n.ToLowerInvariant()).ToList(),
                    Plan = result.Route == TurnRoute.Clarify || result.Route == TurnRoute.OffTopic
                        ? null
                        : result.TravelPlan,
                    AssistantReply = result.AssistantReply,
                    Error = result.Error,
                    ProcessingTimeSeconds = stopwatch.Elapsed.TotalSeconds,
                    Provider = provider.ToString(),
                    Model = model
                });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("ANTHROPIC_API_KEY"))
            {
                _logger.LogWarning("Anthropic API key not configured: {Message}", ex.Message);
                return BadRequest(new { error = "Anthropic API key not configured. Use provider=ollama or set ANTHROPIC_API_KEY." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Conversation turn failed");
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }

    [HttpGet("{id}/messages/stream")]
    [EnableRateLimiting("plan")]
    [Produces("text/event-stream")]
    public async Task StreamMessage(
        string id,
        [FromQuery] string? message,
        [FromQuery] string? provider,
        CancellationToken cancellationToken)
    {
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");
        Response.ContentType = "text/event-stream";

        // Query-param validation — [FromQuery] doesn't run model validation
        if (string.IsNullOrWhiteSpace(message) || message.Length < 10)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await SendSseEvent("error", new { agent = "system", status = "error", content = "Message must be at least 10 characters." });
            return;
        }
        if (message.Length > 2000)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await SendSseEvent("error", new { agent = "system", status = "error", content = "Message must not exceed 2000 characters." });
            return;
        }

        // Acquire the per-conversation lock before opening the stream — if busy, return 409
        // with no SSE preamble so the client's fetch sees a normal HTTP error.
        IAsyncDisposable lockHandle;
        try
        {
            lockHandle = await _locks.AcquireAsync(id, cancellationToken);
        }
        catch (ConversationBusyException)
        {
            Response.StatusCode = StatusCodes.Status409Conflict;
            Response.ContentType = "application/json";
            await Response.WriteAsync("""{"error":"Conversation is currently processing another message."}""", cancellationToken);
            return;
        }

        await using var _lock = lockHandle;

        var conv = await _store.GetAsync(id, cancellationToken);
        if (conv == null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            await SendSseEvent("error", new { agent = "system", status = "error", content = "Conversation not found." });
            return;
        }

        try
        {
            var (chatClient, providerUsed, model) = ChatClientFactory.CreateWithAutoDetect(provider);
            var service = BuildService(chatClient);

            await SendSseEvent("init", new ConversationInitEventData
            {
                Provider = providerUsed.ToString(),
                Model = model,
                ConversationId = conv.Id
            });

            await foreach (var progress in service.ContinueConversationStreamingAsync(conv, message, cancellationToken))
            {
                switch (progress.Status)
                {
                    case ProgressStatus.Routed when progress.Route is { } route:
                        await SendSseEvent("route", new RouteEventData
                        {
                            Route = route.ToString().ToLowerInvariant(),
                            AgentsToRun = TravelPlannerService.AgentNamesFor(route).Select(n => n.ToLowerInvariant()).ToList()
                        });
                        break;

                    case ProgressStatus.Starting:
                        await SendSseEvent("agent-start", new TravelPlanProgressEvent
                        {
                            Agent = progress.CurrentAgent.ToLowerInvariant(),
                            Status = "started",
                            ProgressPercent = progress.ProgressPercent
                        });
                        break;

                    case ProgressStatus.Processing:
                        await SendSseEvent("content", new TravelPlanProgressEvent
                        {
                            Agent = progress.CurrentAgent.ToLowerInvariant(),
                            Status = "processing",
                            Content = progress.PartialOutput,
                            ProgressPercent = progress.ProgressPercent
                        });
                        break;

                    case ProgressStatus.Completed when progress.CurrentAgent != "Complete":
                        await SendSseEvent("agent-complete", new TravelPlanProgressEvent
                        {
                            Agent = progress.CurrentAgent.ToLowerInvariant(),
                            Status = "completed",
                            ProgressPercent = progress.ProgressPercent
                        });
                        break;

                    case ProgressStatus.Error:
                        await SendSseEvent("error", new TravelPlanProgressEvent
                        {
                            Agent = progress.CurrentAgent.ToLowerInvariant(),
                            Status = "error",
                            Content = progress.PartialOutput,
                            ProgressPercent = progress.ProgressPercent
                        });
                        break;

                    case ProgressStatus.PlanFinal:
                        await SendSseEvent("plan-final", new PlanFinalEventData
                        {
                            Plan = progress.PartialOutput ?? string.Empty
                        });
                        break;

                    case ProgressStatus.Clarified:
                        // Treat clarify/off-topic reply as a chat-style assistant message
                        await SendSseEvent("clarified", new { reply = progress.PartialOutput ?? string.Empty });
                        break;

                    case ProgressStatus.Completed:
                        // The terminal "Complete" event — handled after the loop
                        break;
                }
            }

            // Commit conversation state (history + latestPlan) — service already mutated conv
            await _store.UpdateAsync(conv, cancellationToken);

            await SendSseEvent("complete", new TravelPlanProgressEvent
            {
                Agent = "system",
                Status = "completed",
                ProgressPercent = 100
            });
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Conversation stream cancelled for {ConversationId}", conv.Id);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("ANTHROPIC_API_KEY"))
        {
            _logger.LogWarning("Anthropic API key not configured: {Message}", ex.Message);
            await SendSseEvent("error", new { agent = "system", status = "error", content = "Anthropic API key not configured." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Conversation stream error for {ConversationId}", conv.Id);
            await SendSseEvent("error", new { agent = "system", status = "error", content = ex.Message });
        }
    }

    private TravelPlannerService BuildService(IChatClient chatClient)
        => new(chatClient, _researchTools, _travelTools, _serviceLogger);

    private async Task SendSseEvent<T>(string eventType, T data)
    {
        var json = JsonSerializer.Serialize(data, SseJsonOptions);
        await Response.WriteAsync($"event: {eventType}\n");
        await Response.WriteAsync($"data: {json}\n\n");
        await Response.Body.FlushAsync();
    }
}
