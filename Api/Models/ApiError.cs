namespace LocalAgentTravelPlanner.Api.Models;

/// <summary>
/// Serializable error envelope used by every API endpoint. Keeps the free-form
/// <see cref="Error"/> string for backwards compatibility with the existing frontend
/// while adding a stable <see cref="Code"/> for programmatic classification (metrics,
/// alerting, client-side branching) and an optional <see cref="Extras"/> bag for
/// context (e.g. <c>pendingDecision</c> on 409s).
///
/// Deliberately does not implement RFC 7807 ProblemDetails to avoid breaking the
/// browser client mid-cycle; the shape overlaps enough that a future migration is
/// cheap (add <c>type</c>, <c>title</c>, <c>status</c> aliases).
/// </summary>
public sealed class ApiError
{
    /// <summary>Human-readable message; safe to surface directly in the UI.</summary>
    public required string Error { get; init; }

    /// <summary>Stable machine-readable code from <see cref="ErrorCodes"/>. Never localized.</summary>
    public required string Code { get; init; }

    /// <summary>Optional context (pending decision payload, retry-after seconds, etc.).</summary>
    public IReadOnlyDictionary<string, object?>? Extras { get; init; }

    public static ApiError From(string code, string message, IReadOnlyDictionary<string, object?>? extras = null)
        => new() { Code = code, Error = message, Extras = extras };
}

/// <summary>
/// Central registry of error codes. Kept as constants (not enum) so operators can
/// grep for them in log aggregators and dashboards without needing a lookup table.
/// </summary>
public static class ErrorCodes
{
    // Auth / access
    public const string ApiKeyMissing         = "auth.api_key_missing";
    public const string ProviderNotConfigured = "provider.not_configured";

    // Validation
    public const string ValidationMessageTooShort = "validation.message_too_short";
    public const string ValidationMessageTooLong  = "validation.message_too_long";
    public const string ValidationFeedbackTooLong = "validation.feedback_too_long";

    // Resource state
    public const string ConversationNotFound  = "conversation.not_found";
    public const string ConversationBusy      = "conversation.busy";
    public const string PendingDecisionOpen   = "conversation.pending_decision_open";
    public const string PendingDecisionMissing = "conversation.pending_decision_missing";

    // Upstream / infra
    public const string LlmProviderUnreachable = "upstream.llm_unreachable";
    public const string AggregatorEmpty        = "workflow.aggregator_empty";
    public const string WorkflowFailed         = "workflow.failed";
    public const string InternalError          = "internal.unhandled";

    // Rate limiting
    public const string RateLimited = "rate.limited";
}
