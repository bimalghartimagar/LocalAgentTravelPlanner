using FluentAssertions;
using LocalAgentTravelPlanner.Services.Conversations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace LocalAgentTravelPlanner.Tests.Conversations;

/// <summary>
/// Tests run against a real SQLite file (not :memory:) because the store opens fresh
/// connections per call and SQLite's :memory: DB is scoped to a single connection.
/// Each test gets a temp file in <see cref="IAsyncLifetime"/> setup.
/// </summary>
public class SqliteConversationStoreTests : IAsyncLifetime
{
    private string _dbPath = "";
    private string _connectionString = "";

    public Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"convstore-test-{Guid.NewGuid():n}.db");
        _connectionString = $"Data Source={_dbPath}";
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        // Release pooled connections before deleting the file, otherwise Windows holds locks.
        SqliteConnection.ClearAllPools();
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var path = _dbPath + ext;
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch { /* best effort */ }
            }
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Create_then_Get_round_trips_a_blank_conversation()
    {
        var sut = new SqliteConversationStore(_connectionString);

        var conv = await sut.CreateAsync();
        var loaded = await sut.GetAsync(conv.Id);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(conv.Id);
        loaded.Title.Should().BeNull();
        loaded.LatestPlan.Should().BeNull();
        loaded.History.Should().BeEmpty();
        loaded.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        loaded.LastActivity.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task Update_then_Get_round_trips_history_and_plan()
    {
        var sut = new SqliteConversationStore(_connectionString);
        var conv = await sut.CreateAsync();

        conv.History.Add(new ChatMessage(ChatRole.User, "trip to Kyoto"));
        conv.History.Add(new ChatMessage(ChatRole.Assistant, "Here is your plan"));
        conv.History.Add(new ChatMessage(ChatRole.User, "make day 3 cheaper"));
        conv.History.Add(new ChatMessage(ChatRole.Assistant, "Updated plan markdown"));
        conv.LatestPlan = "# Kyoto plan";
        conv.Title = "Kyoto 5d";

        await sut.UpdateAsync(conv);

        var loaded = await sut.GetAsync(conv.Id);
        loaded.Should().NotBeNull();
        loaded!.History.Should().HaveCount(4);
        loaded.History[0].Role.Should().Be(ChatRole.User);
        loaded.History[0].Text.Should().Be("trip to Kyoto");
        loaded.History[1].Role.Should().Be(ChatRole.Assistant);
        loaded.History[3].Text.Should().Be("Updated plan markdown");
        loaded.LatestPlan.Should().Be("# Kyoto plan");
        loaded.Title.Should().Be("Kyoto 5d");
    }

    [Fact]
    public async Task Update_replaces_all_messages_each_call()
    {
        var sut = new SqliteConversationStore(_connectionString);
        var conv = await sut.CreateAsync();

        conv.History.Add(new ChatMessage(ChatRole.User, "msg 1"));
        conv.History.Add(new ChatMessage(ChatRole.Assistant, "reply 1"));
        await sut.UpdateAsync(conv);

        conv.History.Add(new ChatMessage(ChatRole.User, "msg 2"));
        conv.History.Add(new ChatMessage(ChatRole.Assistant, "reply 2"));
        await sut.UpdateAsync(conv);

        var loaded = await sut.GetAsync(conv.Id);
        loaded!.History.Should().HaveCount(4); // not 6 (no duplicates from re-insert)
        loaded.History.Select(m => m.Text).Should().Equal("msg 1", "reply 1", "msg 2", "reply 2");
    }

    [Fact]
    public async Task Persistence_survives_store_re_instantiation()
    {
        // This is the main reason we picked SQLite — same connection string, different instance,
        // conversation still there.
        var first = new SqliteConversationStore(_connectionString);
        var conv = await first.CreateAsync();
        conv.Title = "persistent";
        conv.History.Add(new ChatMessage(ChatRole.User, "hello"));
        conv.History.Add(new ChatMessage(ChatRole.Assistant, "world"));
        conv.LatestPlan = "plan body";
        await first.UpdateAsync(conv);

        var second = new SqliteConversationStore(_connectionString);
        var loaded = await second.GetAsync(conv.Id);

        loaded.Should().NotBeNull();
        loaded!.Title.Should().Be("persistent");
        loaded.History.Should().HaveCount(2);
        loaded.LatestPlan.Should().Be("plan body");
    }

    [Fact]
    public async Task Delete_cascades_to_messages_and_returns_true_only_on_first_delete()
    {
        var sut = new SqliteConversationStore(_connectionString);
        var conv = await sut.CreateAsync();
        conv.History.Add(new ChatMessage(ChatRole.User, "msg"));
        await sut.UpdateAsync(conv);

        (await sut.DeleteAsync(conv.Id)).Should().BeTrue();
        (await sut.DeleteAsync(conv.Id)).Should().BeFalse();
        (await sut.GetAsync(conv.Id)).Should().BeNull();
    }

    [Fact]
    public async Task List_returns_summaries_ordered_by_lastActivity_desc()
    {
        var sut = new SqliteConversationStore(_connectionString);
        var older = await sut.CreateAsync();
        await Task.Delay(15); // ensure timestamps differ at sub-second granularity
        var newer = await sut.CreateAsync();

        var list = await sut.ListAsync();

        list.Should().HaveCount(2);
        list[0].Id.Should().Be(newer.Id);
        list[1].Id.Should().Be(older.Id);
    }

    [Fact]
    public async Task Update_on_deleted_conversation_throws_KeyNotFound()
    {
        var sut = new SqliteConversationStore(_connectionString);
        var conv = await sut.CreateAsync();
        await sut.DeleteAsync(conv.Id);

        var act = async () =>
        {
            conv.Title = "should not stick";
            await sut.UpdateAsync(conv);
        };

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Get_returns_null_for_missing_id()
    {
        var sut = new SqliteConversationStore(_connectionString);

        var result = await sut.GetAsync("doesnotexist");

        result.Should().BeNull();
    }
}
