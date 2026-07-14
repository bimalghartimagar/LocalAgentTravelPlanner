using LocalAgentTravelPlanner.Services.Conversations;
using Microsoft.Extensions.AI;

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

        /// <summary>
        /// Classifies a follow-up turn to a <see cref="TurnRoute"/>. First turn callers should
        /// skip this and assume <see cref="TurnRoute.Full"/>. On parse failure or LLM error,
        /// falls back to <see cref="TurnRoute.Full"/> (safer to over-run than to under-run).
        /// </summary>
        Task<TurnRoute> RouteAsync(
            IReadOnlyList<ChatMessage> history,
            string newMessage,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Runs one conversation turn and returns the full result. Mutates <paramref name="conversation"/>
        /// in place on success (appends user/assistant messages, updates <c>LatestPlan</c> unless
        /// <see cref="TurnRoute.Clarify"/> or <see cref="TurnRoute.OffTopic"/>). Caller persists.
        /// </summary>
        Task<ConversationTurnResponse> ContinueConversationAsync(
            Conversation conversation,
            string newMessage,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Streaming variant of <see cref="ContinueConversationAsync"/>. Yields one
        /// <see cref="TravelPlanProgress"/> with <see cref="ProgressStatus.Routed"/> first,
        /// then per-agent events, then a terminal <see cref="ProgressStatus.PlanFinal"/> or
        /// <see cref="ProgressStatus.Clarified"/>, then <see cref="ProgressStatus.Completed"/>.
        /// On error, the conversation is not mutated.
        /// </summary>
        IAsyncEnumerable<TravelPlanProgress> ContinueConversationStreamingAsync(
            Conversation conversation,
            string newMessage,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Result of one conversation turn. Extends <see cref="TravelPlanResponse"/> with the route
    /// taken and (for <see cref="TurnRoute.Clarify"/> / <see cref="TurnRoute.OffTopic"/>) a
    /// chat-style assistant reply. For plan-changing routes, the new plan is in
    /// <see cref="TravelPlanResponse.TravelPlan"/>.
    /// </summary>
    public record ConversationTurnResponse : TravelPlanResponse
    {
        /// <summary>Which subset of the pipeline ran this turn.</summary>
        public required TurnRoute Route { get; init; }

        /// <summary>Non-null when the turn produced a chat answer instead of an updated plan.</summary>
        public string? AssistantReply { get; init; }
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

        /// <summary>Set on <see cref="ProgressStatus.Routed"/> events.</summary>
        public TurnRoute? Route { get; init; }
    }

    public enum ProgressStatus
    {
        Starting,
        Processing,
        Completed,
        Error,
        /// <summary>Router has decided which agents will run this turn. <c>Route</c> is set.</summary>
        Routed,
        /// <summary>Aggregator finished and <c>LatestPlan</c> was updated. <c>PartialOutput</c> = full plan markdown.</summary>
        PlanFinal,
        /// <summary>Clarify / off-topic turn finished without changing the plan. <c>PartialOutput</c> = assistant reply.</summary>
        Clarified
    }
}
