namespace LocalAgentTravelPlanner.Services.Conversations;

/// <summary>
/// Per-conversation async mutex. Used to serialize turns on the same conversation so two
/// concurrent client requests (e.g. a second browser tab) can't race on history mutation.
/// Acquisition is non-blocking — if the conversation is busy, <see cref="ConversationBusyException"/>
/// is thrown so the controller can translate it into HTTP 409.
/// </summary>
public interface IConversationLock
{
    /// <summary>
    /// Tries to acquire the lock without waiting. Throws <see cref="ConversationBusyException"/>
    /// if another turn is in flight. Dispose the returned handle to release.
    /// </summary>
    Task<IAsyncDisposable> AcquireAsync(string conversationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Thrown when a turn cannot be started because another turn is already running on the same
/// conversation. Maps to HTTP 409 Conflict.
/// </summary>
public sealed class ConversationBusyException : Exception
{
    /// <summary>Identifier of the conversation that was busy when acquisition was attempted.</summary>
    public string ConversationId { get; }

    /// <param name="conversationId">Identifier of the busy conversation; surfaced in the exception message.</param>
    public ConversationBusyException(string conversationId)
        : base($"Conversation '{conversationId}' is currently processing another turn.")
    {
        ConversationId = conversationId;
    }
}
