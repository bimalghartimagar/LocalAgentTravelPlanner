using FluentAssertions;
using LocalAgentTravelPlanner.Services.Conversations;
using Microsoft.Extensions.AI;

namespace LocalAgentTravelPlanner.Tests.Conversations;

public class InMemoryConversationStoreTests
{
    private sealed class FixedClock(DateTime startUtc) : TimeProvider
    {
        private DateTime _now = startUtc;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
        public override DateTimeOffset GetUtcNow() => new(_now, TimeSpan.Zero);
    }

    [Fact]
    public async Task Create_assigns_unique_id_and_zero_history()
    {
        var sut = new InMemoryConversationStore();

        var a = await sut.CreateAsync();
        var b = await sut.CreateAsync();

        a.Id.Should().NotBeNullOrWhiteSpace();
        b.Id.Should().NotBeNullOrWhiteSpace();
        a.Id.Should().NotBe(b.Id);
        a.History.Should().BeEmpty();
        a.LatestPlan.Should().BeNull();
        a.Title.Should().BeNull();
        a.LastActivity.Should().BeCloseTo(a.CreatedAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Get_returns_null_for_unknown_id()
    {
        var sut = new InMemoryConversationStore();

        var result = await sut.GetAsync("does-not-exist");

        result.Should().BeNull();
    }

    [Fact]
    public async Task Update_persists_history_and_plan_changes()
    {
        var sut = new InMemoryConversationStore();
        var conv = await sut.CreateAsync();

        conv.History.Add(new ChatMessage(ChatRole.User, "trip to Kyoto"));
        conv.History.Add(new ChatMessage(ChatRole.Assistant, "Here is your plan"));
        conv.LatestPlan = "# Kyoto plan";
        conv.Title = "Kyoto trip";

        await sut.UpdateAsync(conv);

        var loaded = await sut.GetAsync(conv.Id);
        loaded.Should().NotBeNull();
        loaded!.History.Should().HaveCount(2);
        loaded.LatestPlan.Should().Be("# Kyoto plan");
        loaded.Title.Should().Be("Kyoto trip");
    }

    [Fact]
    public async Task Delete_returns_true_when_removed_and_false_otherwise()
    {
        var sut = new InMemoryConversationStore();
        var conv = await sut.CreateAsync();

        (await sut.DeleteAsync(conv.Id)).Should().BeTrue();
        (await sut.DeleteAsync(conv.Id)).Should().BeFalse();
        (await sut.GetAsync(conv.Id)).Should().BeNull();
    }

    [Fact]
    public async Task List_returns_summaries_sorted_by_lastActivity_desc()
    {
        var clock = new FixedClock(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var sut = new InMemoryConversationStore(clock);

        var older = await sut.CreateAsync();
        clock.Advance(TimeSpan.FromMinutes(10));
        var newer = await sut.CreateAsync();

        var list = await sut.ListAsync();

        list.Should().HaveCount(2);
        list[0].Id.Should().Be(newer.Id);
        list[1].Id.Should().Be(older.Id);
    }

    [Fact]
    public async Task Conversations_persist_indefinitely_without_TTL()
    {
        // Regression — earlier impl auto-expired idle conversations. With persistence as the
        // contract, conversations must survive arbitrary idle periods.
        var clock = new FixedClock(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var sut = new InMemoryConversationStore(clock);
        var conv = await sut.CreateAsync();

        clock.Advance(TimeSpan.FromDays(365));

        var result = await sut.GetAsync(conv.Id);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task Update_bumps_lastActivity()
    {
        var clock = new FixedClock(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var sut = new InMemoryConversationStore(clock);
        var conv = await sut.CreateAsync();
        var originalActivity = conv.LastActivity;

        clock.Advance(TimeSpan.FromMinutes(5));
        conv.History.Add(new ChatMessage(ChatRole.User, "hi"));
        await sut.UpdateAsync(conv);

        var loaded = await sut.GetAsync(conv.Id);
        loaded!.LastActivity.Should().BeAfter(originalActivity);
    }
}
