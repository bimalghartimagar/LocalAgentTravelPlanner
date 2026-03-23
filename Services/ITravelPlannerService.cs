namespace LocalAgentTravelPlanner.Services
{
    /// <summary>
    /// Service interface for travel planning operations.
    ///
    /// WHY AN INTERFACE?
    /// 1. Testability: Can mock this in unit tests
    /// 2. Dependency Injection: ASP.NET Core DI container needs interfaces
    /// 3. Flexibility: Can swap implementations (Ollama vs Azure OpenAI vs Claude)
    /// 4. Separation of Concerns: Controllers/Program.cs don't know about agents
    ///
    /// This is the "Service Layer" pattern - common in enterprise applications.
    /// </summary>
    public interface ITravelPlannerService
    {
        /// <summary>
        /// Processes a travel request through the full agent pipeline.
        /// </summary>
        /// <param name="request">User's travel request (e.g., "3-day trip to Kyoto, budget $800")</param>
        /// <param name="cancellationToken">Cancellation token for async operations</param>
        /// <returns>The complete travel plan response</returns>
        Task<TravelPlanResponse> PlanTravelAsync(string request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Streams the travel planning process, yielding updates as each agent completes.
        /// Useful for real-time UI updates (e.g., SignalR, Server-Sent Events).
        /// </summary>
        /// <param name="request">User's travel request</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Async stream of progress updates</returns>
        IAsyncEnumerable<TravelPlanProgress> PlanTravelStreamingAsync(
            string request,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Response from the travel planning service.
    /// This is a DTO (Data Transfer Object) that can be serialized to JSON for API responses.
    /// </summary>
    public record TravelPlanResponse
    {
        /// <summary>Whether the planning was successful</summary>
        public required bool Success { get; init; }

        /// <summary>The final formatted travel plan (from Aggregator)</summary>
        public required string TravelPlan { get; init; }

        /// <summary>Error message if planning failed</summary>
        public string? Error { get; init; }

        /// <summary>How long the entire process took</summary>
        public TimeSpan ProcessingTime { get; init; }

        /// <summary>Number of agents that processed the request</summary>
        public int AgentsUsed { get; init; }
    }

    /// <summary>
    /// Progress update during streaming travel planning.
    /// Useful for showing users which agent is currently working.
    /// </summary>
    public record TravelPlanProgress
    {
        /// <summary>Which agent is currently processing</summary>
        public required string CurrentAgent { get; init; }

        /// <summary>Status: Starting, Processing, Completed, Error</summary>
        public required ProgressStatus Status { get; init; }

        /// <summary>Partial output from the current agent (for streaming)</summary>
        public string? PartialOutput { get; init; }

        /// <summary>Progress percentage (0-100)</summary>
        public int ProgressPercent { get; init; }
    }

    public enum ProgressStatus
    {
        Starting,
        Processing,
        Completed,
        Error
    }
}
