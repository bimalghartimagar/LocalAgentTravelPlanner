using System.Collections.Concurrent;

namespace LocalAgentTravelPlanner.Services.Conversations;

/// <summary>
/// Process-local implementation of <see cref="IConversationLock"/>. Each conversation id
/// gets its own <see cref="SemaphoreSlim"/>, lazily created and kept for the process lifetime.
/// Memory growth is bounded by the number of distinct conversation ids ever seen by this
/// process (one SemaphoreSlim is ~120 bytes), so 10k conversations ≈ 1.2 MB — acceptable.
///
/// Not multi-instance safe. If you ever run multiple API instances against the same SQLite
/// file, replace this with a Redis-backed distributed lock; the interface stays the same.
/// </summary>
public sealed class ConversationLockService : IConversationLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async Task<IAsyncDisposable> AcquireAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var sem = _locks.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));

        // WaitAsync(0) returns false immediately if the semaphore is held — that's the
        // signal we translate into HTTP 409. Don't queue, don't wait.
        var acquired = await sem.WaitAsync(0, cancellationToken).ConfigureAwait(false);
        if (!acquired)
            throw new ConversationBusyException(conversationId);

        return new Releaser(sem);
    }

    private sealed class Releaser : IAsyncDisposable
    {
        private SemaphoreSlim? _sem;
        public Releaser(SemaphoreSlim sem) => _sem = sem;

        public ValueTask DisposeAsync()
        {
            var sem = Interlocked.Exchange(ref _sem, null);
            sem?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
