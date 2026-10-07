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

    // 15s is well under the nginx default proxy_read_timeout (60s) and the browser's
    // implicit EventSource watchdogs. Keeps intermediaries from tearing down the socket
    // while Auditor thinks for 30-45s on a slower model.
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    // Serializes writes on a single Response between the main event stream and the
    // keep-alive pumper. HttpResponse.Body is not thread-safe; concurrent writes garble
    // the SSE frame boundaries.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

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
            Turns = conv.Turns.Select(t => new ConversationTurnDto
            {
                TurnIndex = t.TurnIndex,
                Route = t.Route.ToString().ToLowerInvariant(),
                AgentsRun = t.AgentsRun,
                CreatedAt = t.CreatedAt,
                AgentOutputs = t.AgentOutputs,
                DurationMs = t.DurationMs,
                Provider = t.Provider,
                Model = t.Model,
                ChangeSummary = t.ChangeSummary,
                InputTokens = t.InputTokens,
                OutputTokens = t.OutputTokens
            }).ToList(),
            LatestPlan = conv.LatestPlan,
            CreatedAt = conv.CreatedAt,
            LastActivity = conv.LastActivity,
            RequireApproval = conv.RequireApproval,
            PendingDecision = conv.PendingDecision != null ? PendingDecisionDto.From(conv.PendingDecision) : null
        });
    }

    /// <summary>
    /// Updates conversation-scoped settings. Currently the only setting is
    /// <see cref="ConversationSettingsRequest.RequireApproval"/> — the human-in-the-loop gate.
    /// Toggling while a decision is pending is allowed (the pending state persists).
    /// </summary>
    [HttpPatch("{id}/settings")]
    public async Task<IActionResult> UpdateSettings(
        string id,
        [FromBody] ConversationSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var conv = await _store.GetAsync(id, cancellationToken);
        if (conv == null) return NotFound();

        if (request.RequireApproval is bool value)
            conv.RequireApproval = value;

        await _store.UpdateAsync(conv, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Clears a pending decision without approving or rejecting. Used when the user wants to
    /// walk away from the paused turn — the previously committed plan (if any) is unchanged
    /// and future turns become unblocked.
    /// </summary>
    [HttpDelete("{id}/pending")]
    public async Task<IActionResult> CancelPending(string id, CancellationToken cancellationToken)
    {
        // Acquire the lock so we don't fight an in-flight decision request.
        IAsyncDisposable lockHandle;
        try
        {
            lockHandle = await _locks.AcquireAsync(id, cancellationToken);
        }
        catch (ConversationBusyException)
        {
            return Conflict(ApiError.From(ErrorCodes.ConversationBusy, "Conversation is currently processing."));
        }

        await using (lockHandle)
        {
            var conv = await _store.GetAsync(id, cancellationToken);
            if (conv == null) return NotFound();
            if (conv.PendingDecision == null) return NoContent();

            conv.PendingDecision = null;
            await _store.UpdateAsync(conv, cancellationToken);
            return NoContent();
        }
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
            return Conflict(ApiError.From(
                ErrorCodes.ConversationBusy,
                "Conversation is currently processing another message. Wait for it to finish and try again."));
        }

        await using (lockHandle)
        {
            var conv = await _store.GetAsync(id, cancellationToken);
            if (conv == null) return NotFound();

            // Block new turns while a decision is outstanding — client must resolve or cancel
            // the pending state before sending another message.
            if (conv.PendingDecision != null)
                return Conflict(ApiError.From(
                    ErrorCodes.PendingDecisionOpen,
                    "This conversation has a pending decision. Approve, reject, or cancel it before sending a new message.",
                    new Dictionary<string, object?>
                    {
                        ["pendingDecision"] = PendingDecisionDto.From(conv.PendingDecision)
                    }));

            var stopwatch = Stopwatch.StartNew();

            try
            {
                var (chatClient, provider, model) = ChatClientFactory.CreateWithAutoDetect(request.Provider);
                var service = BuildService(chatClient, provider.ToString(), model);

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
                    PendingDecision = result.PendingDecision != null ? PendingDecisionDto.From(result.PendingDecision) : null,
                    Error = result.Error,
                    ProcessingTimeSeconds = stopwatch.Elapsed.TotalSeconds,
                    Provider = provider.ToString(),
                    Model = model
                });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("ANTHROPIC_API_KEY"))
            {
                _logger.LogWarning("Anthropic API key not configured: {Message}", ex.Message);
                return BadRequest(ApiError.From(
                    ErrorCodes.ProviderNotConfigured,
                    "Anthropic API key not configured. Use provider=ollama or set ANTHROPIC_API_KEY."));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Conversation turn failed");
                return StatusCode(500, ApiError.From(ErrorCodes.InternalError, ex.Message));
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
            var payload = JsonSerializer.Serialize(ApiError.From(
                ErrorCodes.ConversationBusy,
                "Conversation is currently processing another message. Wait for it to finish and try again."),
                SseJsonOptions);
            await Response.WriteAsync(payload, cancellationToken);
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

        // Same gate as POST: block until pending decision is resolved.
        if (conv.PendingDecision != null)
        {
            Response.StatusCode = StatusCodes.Status409Conflict;
            Response.ContentType = "application/json";
            var payload = JsonSerializer.Serialize(ApiError.From(
                ErrorCodes.PendingDecisionOpen,
                "This conversation has a pending decision. Approve, reject, or cancel it before sending a new message.",
                new Dictionary<string, object?>
                {
                    ["pendingDecision"] = PendingDecisionDto.From(conv.PendingDecision)
                }),
                SseJsonOptions);
            await Response.WriteAsync(payload, cancellationToken);
            return;
        }

        try
        {
            var (chatClient, providerUsed, model) = ChatClientFactory.CreateWithAutoDetect(provider);
            var service = BuildService(chatClient, providerUsed.ToString(), model);

            await SendSseEvent("init", new ConversationInitEventData
            {
                Provider = providerUsed.ToString(),
                Model = model,
                ConversationId = conv.Id
            });

            await RunWithKeepAliveAsync(async () =>
            {
                await foreach (var progress in service.ContinueConversationStreamingAsync(conv, message, cancellationToken))
                {
                    await EmitProgressAsync(progress);
                }
            }, cancellationToken);

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

    /// <summary>
    /// SSE-based resolver for a pending decision. Approve = Aggregator runs and emits
    /// PlanFinal. Reject with feedback + replan = a synthetic Replan turn streams next.
    /// Reject without replan = a short clarify-style event, plan unchanged.
    /// </summary>
    [HttpGet("{id}/decision/stream")]
    [EnableRateLimiting("plan")]
    [Produces("text/event-stream")]
    public async Task StreamDecision(
        string id,
        [FromQuery] bool approve,
        [FromQuery] string? feedback,
        [FromQuery] bool replan,
        [FromQuery] string? provider,
        CancellationToken cancellationToken)
    {
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");
        Response.ContentType = "text/event-stream";

        if (!string.IsNullOrEmpty(feedback) && feedback.Length > 2000)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await SendSseEvent("error", new { agent = "system", status = "error", content = "Feedback must not exceed 2000 characters." });
            return;
        }

        IAsyncDisposable lockHandle;
        try
        {
            lockHandle = await _locks.AcquireAsync(id, cancellationToken);
        }
        catch (ConversationBusyException)
        {
            Response.StatusCode = StatusCodes.Status409Conflict;
            Response.ContentType = "application/json";
            var payload = JsonSerializer.Serialize(ApiError.From(
                ErrorCodes.ConversationBusy,
                "Conversation is currently processing another request."),
                SseJsonOptions);
            await Response.WriteAsync(payload, cancellationToken);
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

        if (conv.PendingDecision == null)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await SendSseEvent("error", new { agent = "system", status = "error", content = "No pending decision to resolve." });
            return;
        }

        try
        {
            var (chatClient, providerUsed, model) = ChatClientFactory.CreateWithAutoDetect(provider);
            var service = BuildService(chatClient, providerUsed.ToString(), model);

            await SendSseEvent("init", new ConversationInitEventData
            {
                Provider = providerUsed.ToString(),
                Model = model,
                ConversationId = conv.Id
            });

            await RunWithKeepAliveAsync(async () =>
            {
                await foreach (var progress in service.ResolveDecisionStreamingAsync(
                    conv, approve, feedback, replan, cancellationToken))
                {
                    await EmitProgressAsync(progress);
                }
            }, cancellationToken);

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
            _logger.LogInformation("Decision stream cancelled for {ConversationId}", conv.Id);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Decision resolution failed for {ConversationId}", conv.Id);
            await SendSseEvent("error", new { agent = "system", status = "error", content = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Decision stream error for {ConversationId}", conv.Id);
            await SendSseEvent("error", new { agent = "system", status = "error", content = ex.Message });
        }
    }

    /// <summary>
    /// Fanout helper — the streaming message and streaming decision endpoints emit the same
    /// per-agent events. Extracted so both call sites stay in lockstep on event shapes.
    /// </summary>
    private async Task EmitProgressAsync(TravelPlanProgress progress)
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
                    Plan = progress.PartialOutput ?? string.Empty,
                    ChangeSummary = progress.ChangeSummary
                });
                break;

            case ProgressStatus.Clarified:
                await SendSseEvent("clarified", new { reply = progress.PartialOutput ?? string.Empty });
                break;

            case ProgressStatus.ApprovalRequired when progress.PendingDecision is { } pd:
                await SendSseEvent("approval-required", PendingDecisionDto.From(pd));
                break;

            case ProgressStatus.Completed:
                // Terminal event — the caller sends its own "complete" wrapper.
                break;
        }
    }

    private TravelPlannerService BuildService(IChatClient chatClient, string? providerName = null, string? modelName = null)
    {
        // Opt-in cheaper router — only built if ROUTER_PROVIDER or ROUTER_MODEL is set.
        // Otherwise the main client handles route classification (existing behavior).
        var router = ChatClientFactory.CreateRouterClientIfConfigured();
        return new TravelPlannerService(
            chatClient, _researchTools, _travelTools, _serviceLogger, providerName, modelName,
            routerChatClient: router?.Client);
    }

    private async Task SendSseEvent<T>(string eventType, T data)
    {
        var json = JsonSerializer.Serialize(data, SseJsonOptions);
        await _writeLock.WaitAsync();
        try
        {
            await Response.WriteAsync($"event: {eventType}\n");
            await Response.WriteAsync($"data: {json}\n\n");
            await Response.Body.FlushAsync();
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>
    /// Runs the SSE body while a background ping loop emits <c>: keep-alive</c> comment
    /// frames every <see cref="KeepAliveInterval"/>. The comment is invisible to browser
    /// EventSource consumers but keeps nginx / cloudflare / whatever reverse proxy from
    /// timing out an idle stream during long agent phases.
    /// </summary>
    private async Task RunWithKeepAliveAsync(Func<Task> body, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var keepAlive = KeepAlivePumpAsync(cts.Token);
        try
        {
            await body();
        }
        finally
        {
            cts.Cancel();
            try { await keepAlive; } catch { /* swallow cancellation from pump */ }
        }
    }

    private async Task KeepAlivePumpAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(KeepAliveInterval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                await _writeLock.WaitAsync(ct);
                try
                {
                    // Comment frame per SSE spec: lines starting with `:` are ignored by clients.
                    await Response.WriteAsync(": keep-alive\n\n", ct);
                    await Response.Body.FlushAsync(ct);
                }
                catch (ObjectDisposedException) { break; }
                catch (InvalidOperationException) { break; }
                finally { _writeLock.Release(); }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
    }
}
