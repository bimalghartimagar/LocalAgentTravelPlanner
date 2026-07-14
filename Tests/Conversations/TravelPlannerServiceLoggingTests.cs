using FluentAssertions;
using LocalAgentTravelPlanner.Services;
using LocalAgentTravelPlanner.Services.Conversations;
using LocalAgentTravelPlanner.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;

namespace LocalAgentTravelPlanner.Tests.Conversations;

/// <summary>
/// Verifies that <see cref="TravelPlannerService"/> emits a structured turn-completion
/// log event with the fields downstream sinks (console, rolling file, observability
/// backends) need to slice by ConversationId / Route / Success without parsing the
/// message text.
/// </summary>
public class TravelPlannerServiceLoggingTests
{
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var entry = new LogEntry
            {
                Level = logLevel,
                Message = formatter(state, exception)
            };
            if (state is IEnumerable<KeyValuePair<string, object?>> kvps)
                foreach (var kvp in kvps)
                    entry.State[kvp.Key] = kvp.Value;
            Entries.Add(entry);
        }
    }

    private sealed class LogEntry
    {
        public LogLevel Level { get; init; }
        public string Message { get; init; } = "";
        public Dictionary<string, object?> State { get; } = new();
    }

    private static TravelPlannerService BuildSut(string routerReply, CapturingLogger<TravelPlannerService> logger)
    {
        var chat = new Mock<IChatClient>();
        chat.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, routerReply)));

        return new TravelPlannerService(
            chat.Object,
            new ResearchTools(new HttpClient()),
            new TravelTools(new HttpClient()),
            logger);
    }

    [Fact]
    public async Task ContinueConversationAsync_offtopic_emits_completed_log_with_structured_fields()
    {
        var logger = new CapturingLogger<TravelPlannerService>();
        var sut = BuildSut(routerReply: "offtopic", logger);

        var conv = new Conversation
        {
            Id = "conv-test-1",
            CreatedAt = DateTime.UtcNow,
            LastActivity = DateTime.UtcNow
        };
        // History non-empty so the router runs (first turn skips it)
        conv.History.Add(new ChatMessage(ChatRole.User, "prior question"));
        conv.History.Add(new ChatMessage(ChatRole.Assistant, "prior answer"));

        await sut.ContinueConversationAsync(conv, "tell me a joke");

        var entry = logger.Entries.SingleOrDefault(e => e.Message.Contains("Conversation turn completed"));
        entry.Should().NotBeNull("a turn-completion log should be emitted");

        entry!.Level.Should().Be(LogLevel.Information);
        entry.State.Should().ContainKey("ConversationId").WhoseValue.Should().Be("conv-test-1");
        entry.State.Should().ContainKey("Route").WhoseValue.Should().Be("OffTopic");
        entry.State.Should().ContainKey("Success").WhoseValue.Should().Be(true);
        entry.State.Should().ContainKey("AgentsRun");
        entry.State.Should().ContainKey("DurationMs");
        entry.State["DurationMs"].Should().BeOfType<long>().Which.Should().BeGreaterThanOrEqualTo(0L);
        entry.State.Should().ContainKey("Error").WhoseValue.Should().BeNull();
    }

    [Fact]
    public async Task RouteAsync_failure_logs_warning_with_exception()
    {
        var logger = new CapturingLogger<TravelPlannerService>();
        var chat = new Mock<IChatClient>();
        chat.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network down"));

        var sut = new TravelPlannerService(
            chat.Object,
            new ResearchTools(new HttpClient()),
            new TravelTools(new HttpClient()),
            logger);

        var route = await sut.RouteAsync(new List<ChatMessage> { new(ChatRole.User, "prior") }, "follow-up");

        route.Should().Be(TurnRoute.Full); // fallback
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("Router LLM call failed"));
    }
}
