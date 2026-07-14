using FluentAssertions;
using LocalAgentTravelPlanner.Services;
using LocalAgentTravelPlanner.Services.Conversations;
using LocalAgentTravelPlanner.Tools;
using Microsoft.Extensions.AI;
using Moq;

namespace LocalAgentTravelPlanner.Tests.Conversations;

public class TravelPlannerServiceRouteTests
{
    private static (TravelPlannerService Sut, Mock<IChatClient> Chat) BuildSut(string routeReply)
    {
        // Loose mock — ChatClientAgent's ctor probes IChatClient.GetService for metadata,
        // and we don't want unrelated probes to fail the test.
        var chat = new Mock<IChatClient>();
        chat
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, routeReply)));

        // Tools are not exercised by RouteAsync; pass dummy ones bound to a never-called HttpClient.
        var researchTools = new ResearchTools(new HttpClient());
        var travelTools = new TravelTools(new HttpClient());
        var sut = new TravelPlannerService(chat.Object, researchTools, travelTools);
        return (sut, chat);
    }

    [Theory]
    [InlineData("full", TurnRoute.Full)]
    [InlineData("FULL", TurnRoute.Full)]
    [InlineData("  Full  ", TurnRoute.Full)]
    [InlineData("replan", TurnRoute.Replan)]
    [InlineData("rebudget", TurnRoute.Rebudget)]
    [InlineData("reaudit", TurnRoute.Reaudit)]
    [InlineData("clarify", TurnRoute.Clarify)]
    [InlineData("offtopic", TurnRoute.OffTopic)]
    public async Task RouteAsync_parses_known_tokens(string reply, TurnRoute expected)
    {
        var (sut, _) = BuildSut(reply);

        var route = await sut.RouteAsync(
            new List<ChatMessage> { new(ChatRole.User, "trip to kyoto") },
            "make day 3 cheaper");

        route.Should().Be(expected);
    }

    [Fact]
    public async Task RouteAsync_falls_back_to_Full_on_unknown_token()
    {
        var (sut, _) = BuildSut("¯\\_(ツ)_/¯");

        var route = await sut.RouteAsync(
            new List<ChatMessage> { new(ChatRole.User, "prior") },
            "new message");

        route.Should().Be(TurnRoute.Full);
    }

    [Fact]
    public async Task RouteAsync_falls_back_to_Full_when_chat_client_throws()
    {
        var chat = new Mock<IChatClient>();
        chat
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network down"));

        var sut = new TravelPlannerService(chat.Object, new ResearchTools(new HttpClient()), new TravelTools(new HttpClient()));

        var route = await sut.RouteAsync(new List<ChatMessage> { new(ChatRole.User, "x") }, "y");

        route.Should().Be(TurnRoute.Full);
    }

    [Fact]
    public void AgentNamesFor_returns_expected_subsets()
    {
        TravelPlannerService.AgentNamesFor(TurnRoute.Full).Should().Equal("Researcher", "Planner", "Accountant", "Auditor", "Aggregator");
        TravelPlannerService.AgentNamesFor(TurnRoute.Replan).Should().Equal("Planner", "Accountant", "Auditor", "Aggregator");
        TravelPlannerService.AgentNamesFor(TurnRoute.Rebudget).Should().Equal("Accountant", "Auditor", "Aggregator");
        TravelPlannerService.AgentNamesFor(TurnRoute.Reaudit).Should().Equal("Auditor", "Aggregator");
        TravelPlannerService.AgentNamesFor(TurnRoute.Clarify).Should().Equal("Aggregator");
        TravelPlannerService.AgentNamesFor(TurnRoute.OffTopic).Should().BeEmpty();
    }

    [Fact]
    public async Task ContinueConversationAsync_offTopic_appends_refusal_without_touching_LatestPlan()
    {
        var (sut, _) = BuildSut("offtopic");
        var conv = new Conversation
        {
            Id = "c1",
            CreatedAt = DateTime.UtcNow,
            LastActivity = DateTime.UtcNow,
            LatestPlan = "prior-plan"
        };
        // History is non-empty so RouteAsync runs (first-turn skips router)
        conv.History.Add(new ChatMessage(ChatRole.User, "prior question"));
        conv.History.Add(new ChatMessage(ChatRole.Assistant, "prior answer"));

        var result = await sut.ContinueConversationAsync(conv, "tell me a joke");

        result.Route.Should().Be(TurnRoute.OffTopic);
        result.Success.Should().BeTrue();
        result.AssistantReply.Should().NotBeNullOrEmpty();
        conv.LatestPlan.Should().Be("prior-plan"); // not mutated
        conv.History.Should().HaveCount(4);
        conv.History[^1].Role.Should().Be(ChatRole.Assistant);
    }
}
