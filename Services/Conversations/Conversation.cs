using Microsoft.Extensions.AI;

namespace LocalAgentTravelPlanner.Services.Conversations;

/// <summary>
/// Routes a single turn to a subset of the 5-agent pipeline.
/// Picked by <see cref="ITravelPlannerService.RouteAsync"/> based on the new user message
/// and the prior conversation history. First turn is always <see cref="Full"/>.
/// </summary>
public enum TurnRoute
{
    /// <summary>Researcher → Planner → Accountant → Auditor → Aggregator.</summary>
    Full,
    /// <summary>Planner → Accountant → Auditor → Aggregator. Itinerary tweaks within the same trip.</summary>
    Replan,
    /// <summary>Accountant → Auditor → Aggregator. Budget/cost adjustments only.</summary>
    Rebudget,
    /// <summary>Auditor → Aggregator. Re-validate existing plan or surface safety/integrity issues.</summary>
    Reaudit,
    /// <summary>Aggregator only. Q&amp;A about the existing plan; <c>LatestPlan</c> is not modified.</summary>
    Clarify,
    /// <summary>No agents run. Reuses <c>NotTravelRefusal</c>.</summary>
    OffTopic
}

/// <summary>
/// A multi-turn travel-planning conversation. Owned by <see cref="IConversationStore"/>;
/// callers must round-trip via <c>UpdateAsync</c> after mutation in the in-memory store
/// (the implementation snapshots on write to stay safe under concurrent reads).
/// </summary>
public sealed class Conversation
{
    /// <summary>Server-assigned identifier (32-char hex GUID).</summary>
    public required string Id { get; init; }

    /// <summary>Display label for the sidebar; derived from the first user message.</summary>
    public string? Title { get; set; }

    /// <summary>
    /// User and assistant turns only — no per-agent intermediate messages. This is what we
    /// feed into the workflow as <c>List&lt;ChatMessage&gt;</c> on follow-up turns.
    /// </summary>
    public List<ChatMessage> History { get; init; } = new();

    /// <summary>Last successful Aggregator output (markdown). Drives the right-pane plan view.</summary>
    public string? LatestPlan { get; set; }

    /// <summary>UTC timestamp the conversation was created.</summary>
    public required DateTime CreatedAt { get; init; }

    /// <summary>UTC timestamp of the most recent successful turn; bumped on every <c>UpdateAsync</c>.</summary>
    public DateTime LastActivity { get; set; }

    /// <summary>
    /// Per-turn metadata (route, agents run) parallel to <see cref="History"/>. Enables
    /// the frontend to reconstruct the route chip + mini pipeline dots on a refreshed
    /// browser session, not just plain text bubbles.
    /// </summary>
    public List<TurnMetadata> Turns { get; init; } = new();

    /// <summary>
    /// Human-in-the-loop toggle. When true, plan-changing turns (Full / Replan / Rebudget /
    /// Reaudit) pause after Auditor and wait for a user decision before running Aggregator.
    /// Clarify / OffTopic routes are never gated (no plan mutation to gate).
    /// </summary>
    public bool RequireApproval { get; set; }

    /// <summary>
    /// Non-null while a turn is paused awaiting user approve/reject. Blocks new turns
    /// on the same conversation with HTTP 409 until resolved via the decision endpoint.
    /// Cleared on approve (after Aggregator runs) or reject (after commit/replan).
    /// </summary>
    public PendingDecision? PendingDecision { get; set; }
}

/// <summary>
/// State captured mid-workflow when <see cref="Conversation.RequireApproval"/> is true.
/// The upstream pipeline (Researcher..Auditor) has completed and its output is buffered
/// here; Aggregator runs later on resume via <c>/api/conversations/{id}/decision</c>.
/// </summary>
public sealed record PendingDecision(
    int TurnIndex,
    TurnRoute Route,
    string UserMessage,
    IReadOnlyList<string> AgentsRun,
    IReadOnlyDictionary<string, string> AgentOutputs,
    string? AuditorVerdict,
    DateTime CreatedAt,
    long UpstreamDurationMs,
    string? Provider,
    string? Model,
    /// <summary>Tokens accumulated by the upstream pipeline. Added to Aggregator tokens on resume.</summary>
    long? UpstreamInputTokens = null,
    long? UpstreamOutputTokens = null);

/// <summary>
/// Slim per-turn record. <see cref="TurnIndex"/> is 0-based and matches the pair of
/// user/assistant messages at positions (2*TurnIndex, 2*TurnIndex+1) in <c>History</c>.
/// </summary>
public sealed record TurnMetadata(
    int TurnIndex,
    TurnRoute Route,
    IReadOnlyList<string> AgentsRun,
    DateTime CreatedAt,
    IReadOnlyDictionary<string, string>? AgentOutputs = null,
    long? DurationMs = null,
    string? Provider = null,
    string? Model = null,
    string? ChangeSummary = null,
    /// <summary>Sum of input tokens across every LLM call this turn (router + all agents + tool sub-turns).</summary>
    long? InputTokens = null,
    /// <summary>Sum of output tokens across every LLM call this turn.</summary>
    long? OutputTokens = null);

/// <summary>Lightweight projection for the sidebar list.</summary>
public sealed record ConversationSummary(string Id, string? Title, DateTime LastActivity);
