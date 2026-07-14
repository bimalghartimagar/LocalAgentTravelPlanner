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
    public string? LatestPlan { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime LastActivity { get; init; }
}

public class ConversationMessageDto
{
    public required string Role { get; init; } // "user" | "assistant"
    public required string Content { get; init; }
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
}

/// <summary>Payload for the <c>init</c> SSE event on the conversation endpoint.</summary>
public class ConversationInitEventData
{
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required string ConversationId { get; init; }
}
