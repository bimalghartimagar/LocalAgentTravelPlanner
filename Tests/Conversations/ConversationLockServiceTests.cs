using FluentAssertions;
using LocalAgentTravelPlanner.Services.Conversations;

namespace LocalAgentTravelPlanner.Tests.Conversations;

public class ConversationLockServiceTests
{
    [Fact]
    public async Task First_Acquire_succeeds()
    {
        var sut = new ConversationLockService();

        await using var handle = await sut.AcquireAsync("conv-1");

        handle.Should().NotBeNull();
    }

    [Fact]
    public async Task Second_Acquire_on_held_lock_throws_ConversationBusy()
    {
        var sut = new ConversationLockService();
        var first = await sut.AcquireAsync("conv-1");

        var act = async () => await sut.AcquireAsync("conv-1");

        await act.Should().ThrowAsync<ConversationBusyException>()
            .Where(ex => ex.ConversationId == "conv-1");

        await first.DisposeAsync();
    }

    [Fact]
    public async Task Acquire_succeeds_after_first_holder_disposes()
    {
        var sut = new ConversationLockService();
        var first = await sut.AcquireAsync("conv-1");
        await first.DisposeAsync();

        await using var second = await sut.AcquireAsync("conv-1");

        second.Should().NotBeNull();
    }

    [Fact]
    public async Task Different_conversations_do_not_block_each_other()
    {
        var sut = new ConversationLockService();

        await using var a = await sut.AcquireAsync("conv-a");
        await using var b = await sut.AcquireAsync("conv-b");

        a.Should().NotBeNull();
        b.Should().NotBeNull();
    }

    [Fact]
    public async Task Dispose_is_idempotent()
    {
        var sut = new ConversationLockService();
        var handle = await sut.AcquireAsync("conv-1");

        await handle.DisposeAsync();
        var act = async () => await handle.DisposeAsync(); // double-dispose

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Throws_for_null_or_empty_id()
    {
        var sut = new ConversationLockService();

        await FluentActions.Invoking(() => sut.AcquireAsync(null!)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Invoking(() => sut.AcquireAsync("")).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Invoking(() => sut.AcquireAsync("   ")).Should().ThrowAsync<ArgumentException>();
    }
}
