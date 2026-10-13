using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// <c>IKeyAllocator</c>'s own contract (quest 0328001a). The concurrency claim is proved against Postgres in
/// <c>Integration/KeyAllocatorConcurrencyTests</c>; these pin the pieces an in-memory store can show.
/// </summary>
public sealed class KeyAllocatorTests
{
    private static ApplicationDbContext Context(string name) =>
        TestTenancy.Context(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(name).Options,
            TestTenancy.Caller(1));

    [Fact]
    public async Task A_key_allocated_outside_InsertAsync_throws_because_nothing_would_retry_it()
    {
        await using var context = Context($"keys-outside-{Guid.NewGuid():N}");

        var act = () => TestKeys.Allocator.NextKeyAsync(context.Rooms, r => r.RoomId, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Room*outside IKeyAllocator.InsertAsync*");
    }

    [Fact]
    public async Task The_next_key_is_one_past_the_scope_max_and_one_for_an_empty_scope()
    {
        var name = $"keys-next-{Guid.NewGuid():N}";
        await using (var seed = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(name).Options))
        {
            seed.Rooms.AddRange(
                new Room { HouseholdId = 1, RoomId = 4, Name = "Mine" },
                new Room { HouseholdId = 2, RoomId = 9, Name = "Theirs" });
            await seed.SaveChangesAsync();
        }

        var (mine, emptyParent) = await TestKeys.Allocator.InsertAsync("Test", async _ =>
        {
            await using var context = Context(name);
            return (
                await TestKeys.Allocator.NextKeyAsync(context.Rooms, r => r.RoomId, CancellationToken.None),
                await TestKeys.Allocator.NextKeyAsync(
                    context.ShoppingListItems.Where(i => i.ShoppingListId == 7), i => i.ItemId, CancellationToken.None));
        });

        mine.Should().Be(5, "household 2's room 9 is outside the caller's tenant");
        emptyParent.Should().Be(1);
    }

    [Fact]
    public async Task A_snapshot_key_is_one_past_the_snapshot_and_refused_outside_InsertAsync()
    {
        var keys = await TestKeys.Allocator.InsertAsync("Test", _ => Task.FromResult(
            (TestKeys.Allocator.NextKeyFromSnapshot([3, 7, 5]), TestKeys.Allocator.NextKeyFromSnapshot([]))));

        keys.Should().Be((8, 1));
        FluentActions.Invoking(() => TestKeys.Allocator.NextKeyFromSnapshot([1]))
            .Should().Throw<InvalidOperationException>().WithMessage("*outside IKeyAllocator.InsertAsync*");
    }

    [Fact]
    public async Task After_InsertAsync_returns_the_flow_is_outside_again()
    {
        await TestKeys.Allocator.InsertAsync("Test", _ => Task.FromResult(0));
        await using var context = Context($"keys-after-{Guid.NewGuid():N}");

        var act = () => TestKeys.Allocator.NextKeyAsync(context.Rooms, r => r.RoomId, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
