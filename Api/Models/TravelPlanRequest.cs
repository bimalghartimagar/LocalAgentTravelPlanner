using System.ComponentModel.DataAnnotations;

namespace LocalAgentTravelPlanner.Api.Models;

/// <summary>
/// Request model for creating a travel plan.
/// </summary>
public class TravelPlanRequest
{
    /// <summary>
    /// Natural language description of the trip.
    /// Example: "3-day family trip from Butwal to Pokhara, budget 50000 NPR"
    /// </summary>
    [Required]
    [MinLength(10, ErrorMessage = "Request must be at least 10 characters")]
    public required string Request { get; init; }

    /// <summary>
    /// Optional: Preferred LLM provider (ollama or anthropic).
    /// If not specified, auto-detects based on available API keys.
    /// </summary>
    public string? Provider { get; init; }
}

/// <summary>
/// Response model for a completed travel plan.
/// </summary>
public class TravelPlanApiResponse
{
    public required bool Success { get; init; }
    public required string TravelPlan { get; init; }
    public string? Error { get; init; }
    public double ProcessingTimeSeconds { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
}

/// <summary>
/// Server-Sent Event data for streaming progress.
/// </summary>
public class TravelPlanProgressEvent
{
    public required string Agent { get; init; }
    public required string Status { get; init; }
    public string? Content { get; init; }
    public int ProgressPercent { get; init; }
}
