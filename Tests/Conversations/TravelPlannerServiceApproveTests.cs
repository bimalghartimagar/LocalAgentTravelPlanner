using System.Runtime.CompilerServices;
using FluentAssertions;
using LocalAgentTravelPlanner.Services;
using LocalAgentTravelPlanner.Services.Conversations;
using LocalAgentTravelPlanner.Tools;
using Microsoft.Extensions.AI;

namespace LocalAgentTravelPlanner.Tests.Conversations;

/// <summary>
/// Exercises the approve path of <see cref="TravelPlannerService.ResolveDecisionStreamingAsync"/>
/// end-to-end through a real MAF workflow, using a fake <see cref="IChatClient"/> that
/// streams canned Aggregator output. Complements <c>TravelPlannerServiceHitlTests</c>,
/// which covers the reject branch (no LLM call).
/// </summary>
public class TravelPlannerServiceApproveTests
{
    /// <summary>
    /// Minimal fake that answers streaming and non-streaming calls with a canned reply.
    /// Splits the reply into chunks so the workflow's per-token event loop sees multiple
    /// <see cref="AgentRunUpdateEvent"/> ticks (mirrors real provider behavior).
    /// </summary>
    private sealed class FakeStreamingChatClient : IChatClient
    {
        private readonly string _reply;
        public int StreamCalls;
        public int SyncCalls;

        public FakeStreamingChatClient(string reply) { _reply = reply; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            SyncCalls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _reply)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamCalls++;
            const int chunkSize = 40;
            for (var i = 0; i < _reply.Length; i += chunkSize)
            {
                var end = Math.Min(i + chunkSize, _reply.Length);
                var slice = _reply[i..end];
                yield return new ChatResponseUpdate(ChatRole.Assistant, slice);
                await Task.Yield();
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private const string CannedAggregatorPlan = """
        # Kyoto 5-day trip

        ### Plan Status: APPROVED
        > Audit Score: 4.5/5.0
        > All checks passed.

        ## Overview
        Kyoto trip, 5 days, budget $1500.

        ## Itinerary
        Day 1: Fushimi Inari.
        Day 2: Arashiyama.
        """;

    private static Conversation ConvWithPending(TurnRoute route = TurnRoute.Full)
    {
        var conv = new Conversation
        {
            Id = "conv-approve-1",
            CreatedAt = DateTime.UtcNow,
            LastActivity = DateTime.UtcNow,
            LatestPlan = null
        };
        conv.PendingDecision = new PendingDecision(
            TurnIndex: 0,
            Route: route,
            UserMessage: "5-day Kyoto trip, budget $1500",
            AgentsRun: new[] { "researcher", "planner", "accountant", "auditor" },
            AgentOutputs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["researcher"] = "Kyoto weather mild. Hotels: Sakura Inn $150/night.",
                ["planner"] = "Day 1: Fushimi Inari. Day 2: Arashiyama.",
                ["accountant"] = "Total: $1200. Within budget.",
                ["auditor"] = "FINAL VERDICT: APPROVED. Score 4.5/5."
            },
            AuditorVerdict: "APPROVED",
            CreatedAt: DateTime.UtcNow,
            UpstreamDurationMs: 8000,
            Provider: "Fake",
            Model: "fake-model");
        return conv;
    }

    [Fact]
    public async Task Approve_runs_Aggregator_and_commits_plan_turn()
    {
        var fake = new FakeStreamingChatClient(CannedAggregatorPlan);
        var sut = new TravelPlannerService(
            fake,
            new ResearchTools(new HttpClient()),
            new TravelTools(new HttpClient()),
            providerName: "Fake",
            modelName: "fake-model");
        var conv = ConvWithPending();

        var events = new List<TravelPlanProgress>();
        await foreach (var evt in sut.ResolveDecisionStreamingAsync(conv, approve: true, feedback: null, replan: false))
        {
            events.Add(evt);
        }

        // Aggregator must have been invoked exactly once via streaming
        fake.StreamCalls.Should().BeGreaterThan(0, "Aggregator uses the streaming path");

        // Pending state cleared, plan committed to conversation
        conv.PendingDecision.Should().BeNull();
        conv.LatestPlan.Should().NotBeNullOrEmpty();
        conv.LatestPlan.Should().Contain("Kyoto");

        // History gains one user + one assistant turn
        conv.History.Should().HaveCount(2);
        conv.History[0].Role.Should().Be(ChatRole.User);
        conv.History[0].Text.Should().Be("5-day Kyoto trip, budget $1500");
        conv.History[1].Role.Should().Be(ChatRole.Assistant);

        // Turn metadata committed with agent outputs merged
        conv.Turns.Should().HaveCount(1);
        conv.Turns[0].Route.Should().Be(TurnRoute.Full);
        conv.Turns[0].AgentOutputs.Should().NotBeNull();
        conv.Turns[0].AgentOutputs!.Should().ContainKey("aggregator");
        conv.Turns[0].AgentOutputs!["aggregator"].Should().Contain("Kyoto");

        // PlanFinal event emitted with the full plan payload
        events.Should().Contain(e => e.Status == ProgressStatus.PlanFinal);
        var planFinal = events.First(e => e.Status == ProgressStatus.PlanFinal);
        planFinal.PartialOutput.Should().Contain("Kyoto");
    }

    [Fact]
    public async Task Approve_preserves_upstream_agent_content_in_committed_turn()
    {
        var fake = new FakeStreamingChatClient(CannedAggregatorPlan);
        var sut = new TravelPlannerService(
            fake,
            new ResearchTools(new HttpClient()),
            new TravelTools(new HttpClient()));
        var conv = ConvWithPending();

        await foreach (var _ in sut.ResolveDecisionStreamingAsync(conv, approve: true, feedback: null, replan: false)) { }

        var outputs = conv.Turns[0].AgentOutputs!;
        outputs.Should().ContainKey("researcher");
        outputs.Should().ContainKey("planner");
        outputs.Should().ContainKey("accountant");
        outputs.Should().ContainKey("auditor");
        outputs["researcher"].Should().Contain("Sakura Inn");
        outputs["auditor"].Should().Contain("APPROVED");
    }

    [Fact]
    public async Task Approve_emits_ApprovedVerdict_when_Aggregator_agrees_with_Auditor()
    {
        var fake = new FakeStreamingChatClient(CannedAggregatorPlan);
        var sut = new TravelPlannerService(
            fake,
            new ResearchTools(new HttpClient()),
            new TravelTools(new HttpClient()));
        var conv = ConvWithPending();

        await foreach (var _ in sut.ResolveDecisionStreamingAsync(conv, approve: true, feedback: null, replan: false)) { }

        // EnforceAuditorVerdict should be a no-op here — plan already says APPROVED, matching Auditor
        conv.LatestPlan.Should().Contain("Plan Status: APPROVED");
    }

    [Fact]
    public async Task RejectAndReplan_clears_pending_and_chains_new_turn_with_feedback()
    {
        // The replan turn kicks the router (sync) + full pipeline (streaming). Same canned
        // reply feeds every call — router falls through to Full on unrecognized token, and
        // each agent produces the canned text without tool calls.
        const string canned = """
            # Kyoto trip revised

            ### Plan Status: APPROVED
            > Score 4.2/5.0
            > Day 3 dinner swapped.
            """;
        var fake = new FakeStreamingChatClient(canned);
        var sut = new TravelPlannerService(
            fake,
            new ResearchTools(new HttpClient()),
            new TravelTools(new HttpClient()));
        var conv = ConvWithPending();

        await foreach (var _ in sut.ResolveDecisionStreamingAsync(conv, approve: false, feedback: "Day 3 too expensive, cut $100", replan: true)) { }

        conv.PendingDecision.Should().BeNull("reject clears pending before chaining");
        conv.History.Should().HaveCount(2, "the synthetic replan turn committed one user + one assistant pair");
        conv.History[0].Role.Should().Be(ChatRole.User);
        conv.History[0].Text.Should().Contain("Previous draft was rejected");
        conv.History[0].Text.Should().Contain("Day 3 too expensive");
        conv.LatestPlan.Should().NotBeNullOrEmpty();
        conv.Turns.Should().HaveCount(1);
    }

    [Fact]
    public async Task Approve_when_Aggregator_flips_verdict_gets_rewritten_by_enforcer()
    {
        // Simulate weak-model verdict inversion: Aggregator writes REJECTED even though Auditor said APPROVED
        const string inverted = """
            # Kyoto trip

            ### Plan Status: REJECTED
            > Score 4.5/5.0
            """;
        var fake = new FakeStreamingChatClient(inverted);
        var sut = new TravelPlannerService(
            fake,
            new ResearchTools(new HttpClient()),
            new TravelTools(new HttpClient()));
        var conv = ConvWithPending();

        await foreach (var _ in sut.ResolveDecisionStreamingAsync(conv, approve: true, feedback: null, replan: false)) { }

        // Enforcer must have rewritten the plan header back to APPROVED (matching Auditor)
        conv.LatestPlan.Should().Contain("Plan Status: APPROVED");
        conv.LatestPlan.Should().NotContain("Plan Status: REJECTED");
    }
}
