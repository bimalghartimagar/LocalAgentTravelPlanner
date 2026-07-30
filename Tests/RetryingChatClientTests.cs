using FluentAssertions;
using LocalAgentTravelPlanner.Services;
using Microsoft.Extensions.AI;

namespace LocalAgentTravelPlanner.Tests;

/// <summary>
/// Exercises the transient-failure retry middleware via <see cref="HttpRequestException"/>
/// which the middleware treats as transient without any HTTP-specific plumbing. The
/// ClientResultException branch is exercised in production against real OpenAI-compat
/// providers; a proper test double for that type requires reflection into
/// PipelineResponse internals that shift between preview versions.
/// </summary>
public class RetryingChatClientTests
{
    private sealed class StubChatClient : IChatClient
    {
        public int CallCount;
        private readonly Queue<Func<ChatResponse>> _responses;

        public StubChatClient(params Func<ChatResponse>[] responses)
        {
            _responses = new Queue<Func<ChatResponse>>(responses);
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (_responses.Count == 0)
                throw new InvalidOperationException("No more stubbed responses.");
            var producer = _responses.Dequeue();
            var result = producer();
            return Task.FromResult(result);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task Recovers_after_two_transient_HttpRequestExceptions()
    {
        var success = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        var stub = new StubChatClient(
            () => throw new HttpRequestException("connection reset"),
            () => throw new HttpRequestException("dns timeout"),
            () => success);
        var sut = new RetryingChatClient(stub);

        var result = await sut.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") });

        result.Should().BeSameAs(success);
        stub.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task Gives_up_after_MaxRetries_and_rethrows_last_error()
    {
        var stub = new StubChatClient(
            () => throw new HttpRequestException("boom"),
            () => throw new HttpRequestException("boom"),
            () => throw new HttpRequestException("boom"),
            () => throw new HttpRequestException("boom"));
        var sut = new RetryingChatClient(stub);

        var act = async () => await sut.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") });

        await act.Should().ThrowAsync<HttpRequestException>();
        stub.CallCount.Should().Be(4, "MaxRetries=3 means 1 initial + 3 retries");
    }

    [Fact]
    public async Task Non_transient_exception_is_not_retried()
    {
        // Plain InvalidOperationException does not match IsTransient — should bubble up
        // immediately without a second call.
        var stub = new StubChatClient(
            () => throw new InvalidOperationException("hard failure"));
        var sut = new RetryingChatClient(stub);

        var act = async () => await sut.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") });

        await act.Should().ThrowAsync<InvalidOperationException>();
        stub.CallCount.Should().Be(1, "non-transient errors get zero retries");
    }

    [Fact]
    public async Task Successful_first_call_makes_no_extra_calls()
    {
        var success = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        var stub = new StubChatClient(() => success);
        var sut = new RetryingChatClient(stub);

        var result = await sut.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") });

        result.Should().BeSameAs(success);
        stub.CallCount.Should().Be(1);
    }
}
