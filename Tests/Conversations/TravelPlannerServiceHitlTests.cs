using FluentAssertions;
using LocalAgentTravelPlanner.Services;
using LocalAgentTravelPlanner.Services.Conversations;
using LocalAgentTravelPlanner.Tools;
using Microsoft.Extensions.AI;
using Moq;

namespace LocalAgentTravelPlanner.Tests.Conversations;

/// <summary>
/// Coverage for the reject-and-close branch of <see cref="TravelPlannerService.ResolveDecisionStreamingAsync"/>
/// and the guard that throws when there is no pending decision. Approve + reject-and-replan
/// both drive a full MAF workflow; those paths are covered by the streaming integration
/// tests when a real IChatClient is available.
/// </summary>
public class TravelPlannerServiceHitlTests
{
    private static TravelPlannerService BuildSut()
    {
        var chat = new Mock<IChatClient>();
        return new TravelPlannerService(
            chat.Object,
            new ResearchTools(new HttpClient()),
            new TravelTools(new HttpClient()));
    }

    private static Conversation ConvWithPending(string? feedback = null)
    {
        var conv = new Conversation
        {
            Id = "conv-1",
            CreatedAt = DateTime.UtcNow,
            LastActivity = DateTime.UtcNow,
            LatestPlan = "existing plan markdown"
        };
        conv.PendingDecision = new PendingDecision(
            TurnIndex: 0,
            Route: TurnRoute.Full,
            UserMessage: "5-day trip to Kyoto",
            AgentsRun: new[] { "researcher", "planner", "accountant", "auditor" },
            AgentOutputs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["auditor"] = "Verdict: APPROVED"
            },
            AuditorVerdict: "APPROVED",
            CreatedAt: DateTime.UtcNow,
            UpstreamDurationMs: 8000,
            Provider: "Anthropic",
            Model: "claude-sonnet-4-5");
        return conv;
    }

    [Fact]
    public async Task ResolveDecisionStreamingAsync_throws_when_no_pending_decision()
    {
        var sut = BuildSut();
        var conv = new Conversation
        {
            Id = "conv-x",
            CreatedAt = DateTime.UtcNow,
            LastActivity = DateTime.UtcNow
        };

        var act = async () =>
        {
            await foreach (var _ in sut.ResolveDecisionStreamingAsync(conv, approve: true, feedback: null, replan: false))
            {
                // drain
            }
        };

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RejectAndClose_commits_rejection_turn_without_touching_LatestPlan()
    {
        var sut = BuildSut();
        var conv = ConvWithPending();
        var priorPlan = conv.LatestPlan;

        var events = new List<TravelPlanProgress>();
        await foreach (var evt in sut.ResolveDecisionStreamingAsync(conv, approve: false, feedback: "too expensive", replan: false))
        {
            events.Add(evt);
        }

        conv.PendingDecision.Should().BeNull("reject must clear the pending state");
        conv.LatestPlan.Should().Be(priorPlan, "reject-and-close never touches LatestPlan");
        conv.History.Should().HaveCount(2, "one user turn + one assistant reply committed");
        conv.History[0].Role.Should().Be(ChatRole.User);
        conv.History[0].Text.Should().Be("5-day trip to Kyoto");
        conv.History[1].Role.Should().Be(ChatRole.Assistant);
        conv.History[1].Text.Should().Contain("too expensive");
        conv.Turns.Should().HaveCount(1);
        conv.Turns[0].Route.Should().Be(TurnRoute.Full);

        events.Should().Contain(e => e.Status == ProgressStatus.Clarified);
        events.Should().Contain(e => e.Status == ProgressStatus.Completed);
    }

    [Fact]
    public async Task RejectAndClose_without_feedback_still_commits_a_reply()
    {
        var sut = BuildSut();
        var conv = ConvWithPending();

        await foreach (var _ in sut.ResolveDecisionStreamingAsync(conv, approve: false, feedback: null, replan: false))
        {
            // drain
        }

        conv.PendingDecision.Should().BeNull();
        conv.History.Should().HaveCount(2);
        conv.History[1].Text.Should().Contain("unchanged");
    }

    [Fact]
    public async Task RejectAndReplan_without_feedback_falls_back_to_close()
    {
        // Empty feedback + replan=true is treated as reject-and-close (no useful user
        // signal to seed a new turn).
        var sut = BuildSut();
        var conv = ConvWithPending();

        await foreach (var _ in sut.ResolveDecisionStreamingAsync(conv, approve: false, feedback: "   ", replan: true))
        {
            // drain
        }

        conv.PendingDecision.Should().BeNull();
        conv.History.Should().HaveCount(2, "no synthetic replan turn was chained");
    }
}
