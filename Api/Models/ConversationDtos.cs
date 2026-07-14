using System.ComponentModel.DataAnnotations;

namespace LocalAgentTravelPlanner.Api.Models;

/// <summary>POST body for <c>/api/conversations/{id}/messages</c>.</summary>
public class ConversationMessageRequest
{
    [Required]
    [MinLength(10, ErrorMessage = "Message must be at least 10 characters")]
    [MaxLength(2000, ErrorMessage = "Message must not exceed 2000 characters")]
    public required string Message { get; init; }

    public string? Provider { get; init; }
}

public class CreateConversationResponse
{
    public required string Id { get; init; }
    public required DateTime CreatedAt { get; init; }
}

public class ConversationSummaryResponse
{
    public required string Id { get; init; }
    public string? Title { get; init; }
    public required DateTime LastActivity { get; init; }
}

public class ConversationDetailResponse
{
    public required string Id { get; init; }
    public string? Title { get; init; }
    public required IReadOnlyList<ConversationMessageDto> History { get; init; }
    public required IReadOnlyList<ConversationTurnDto> Turns { get; init; }
    public string? LatestPlan { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime LastActivity { get; init; }
}

public class ConversationMessageDto
{
    public required string Role { get; init; } // "user" | "assistant"
    public required string Content { get; init; }
}

/// <summary>Per-turn metadata for frontend UI reconstruction on page refresh.</summary>
public class ConversationTurnDto
{
    public required int TurnIndex { get; init; }
    public required string Route { get; init; } // lowercase enum name
    public required IReadOnlyList<string> AgentsRun { get; init; }
    public required DateTime CreatedAt { get; init; }
    /// <summary>Per-agent output text (agent-name-lowercase -> markdown). Null if not captured.</summary>
    public IReadOnlyDictionary<string, string>? AgentOutputs { get; init; }
    public long? DurationMs { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    /// <summary>Aggregator's per-turn "what changed" bullet list. Only present on subset routes.</summary>
    public string? ChangeSummary { get; init; }
}

public class ConversationTurnApiResponse
{
    public required bool Success { get; init; }
    public required string Route { get; init; }
    public required IReadOnlyList<string> AgentsRun { get; init; }
    public string? Plan { get; init; }
    public string? AssistantReply { get; init; }
    public string? Error { get; init; }
    public double ProcessingTimeSeconds { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
}

/// <summary>Payload for the <c>route</c> SSE event.</summary>
public class RouteEventData
{
    public required string Route { get; init; }
    public required IReadOnlyList<string> AgentsToRun { get; init; }
}

/// <summary>Payload for the <c>plan-final</c> SSE event.</summary>
public class PlanFinalEventData
{
    public required string Plan { get; init; }
    /// <summary>Optional "what changed" bullet list for subset-route follow-up turns.</summary>
    public string? ChangeSummary { get; init; }
}

/// <summary>Payload for the <c>init</c> SSE event on the conversation endpoint.</summary>
public class ConversationInitEventData
{
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required string ConversationId { get; init; }
}
