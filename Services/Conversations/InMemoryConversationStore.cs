using System.Collections.Concurrent;

namespace LocalAgentTravelPlanner.Services.Conversations;

/// <summary>
/// Process-local conversation store. Use for tests, ephemeral demos, or when you explicitly
/// don't want disk persistence. Conversations live until <see cref="DeleteAsync"/> is called
/// or the process exits — no idle TTL.
///
/// Not safe across multiple processes; use <see cref="SqliteConversationStore"/> for that.
/// </summary>
public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<string, Conversation> _store = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public InMemoryConversationStore() : this(TimeProvider.System) { }

    /// <summary>Test seam — inject a controllable clock for deterministic LastActivity ordering.</summary>
    public InMemoryConversationStore(TimeProvider clock)
    {
        _clock = clock;
    }

    public Task<Conversation> CreateAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var conv = new Conversation
        {
            Id = Guid.NewGuid().ToString("n"),
            CreatedAt = now,
            LastActivity = now
        };
        _store[conv.Id] = conv;
        return Task.FromResult(conv);
    }

    public Task<Conversation?> GetAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult(_store.TryGetValue(id, out var conv) ? conv : null);

    public Task UpdateAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        conversation.LastActivity = _clock.GetUtcNow().UtcDateTime;
        _store[conversation.Id] = conversation;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult(_store.TryRemove(id, out _));

    public Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var summaries = _store.Values
            .Select(c => new ConversationSummary(c.Id, c.Title, c.LastActivity))
            .OrderByDescending(s => s.LastActivity)
            .ToList();
        return Task.FromResult<IReadOnlyList<ConversationSummary>>(summaries);
    }
}
