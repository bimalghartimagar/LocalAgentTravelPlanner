namespace LocalAgentTravelPlanner.Services.Conversations;

/// <summary>
/// Persistence boundary for multi-turn conversations. The default implementation is
/// in-memory; the interface exists so SQLite/Redis/etc. can be slotted in later without
/// touching the controller or service layer.
/// </summary>
public interface IConversationStore
{
    /// <summary>Allocates a new empty conversation with a server-side GUID id.</summary>
    Task<Conversation> CreateAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the conversation, or null if missing/expired.</summary>
    Task<Conversation?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the latest state. Implementations should treat this as the commit point of a
    /// turn — callers only call <c>UpdateAsync</c> after the workflow completes successfully.
    /// </summary>
    Task UpdateAsync(Conversation conversation, CancellationToken cancellationToken = default);

    /// <summary>Returns true if the conversation existed and was removed.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns summaries for the sidebar, newest LastActivity first. Expired entries are
    /// evicted lazily here so the list stays in sync with what <see cref="GetAsync"/> sees.
    /// </summary>
    Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken cancellationToken = default);
}
