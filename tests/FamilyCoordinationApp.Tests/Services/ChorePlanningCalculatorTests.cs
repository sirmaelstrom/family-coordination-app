using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Dtos;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Unit tests for the pure <see cref="ChorePlanningCalculator"/>. The calculator takes NO clock and NO
/// window — planning is all-time by construction — so these are fully deterministic. No DB, no I/O. The
/// caller is responsible for HouseholdId scoping; these tests feed already-scoped sequences.
/// </summary>
public class ChorePlanningCalculatorTests
{
    private readonly ChorePlanningCalculator _calc = new();

    private static MemberDto Member(int userId, string name) =>
        new(userId, name, name[..2].ToUpperInvariant(), PictureUrl: null);

    private static Chore Chore(int enteredByUserId) =>
        new() { HouseholdId = 1, EnteredByUserId = enteredByUserId };

    private static Recipe Recipe(int? createdByUserId, bool isDeleted = false) =>
        new() { HouseholdId = 1, CreatedByUserId = createdByUserId, IsDeleted = isDeleted };

    private static ShoppingListItem Item(int? addedByUserId, bool isManuallyAdded = true) =>
        new() { HouseholdId = 1, AddedByUserId = addedByUserId, IsManuallyAdded = isManuallyAdded };

    private static ChoreEvent Event(ChoreEventType type, int actorUserId, int? targetUserId) =>
        new() { HouseholdId = 1, Type = type, ActorUserId = actorUserId, TargetUserId = targetUserId };

    private static MemberPlanningDto For(IReadOnlyList<MemberPlanningDto> rows, int userId) =>
        rows.Single(r => r.UserId == userId);

    // ---- Hand-off filter: only true assignments-to-others count, credited to the actor (V6, D6) -------

    [Fact]
    public void Compute_HandOffLane_CountsOnlyHandedOffToOthers_CreditedToActor()
    {
        var members = new[] { Member(1, "Alice"), Member(2, "Bob") };
        var events = new[]
        {
            // Real hand-off to another member → counts, credited to the ACTOR (Alice).
            Event(ChoreEventType.HandedOff, actorUserId: 1, targetUserId: 2),
            // Self-hand-off (target == actor) → excluded by the actor != target guard.
            Event(ChoreEventType.HandedOff, actorUserId: 1, targetUserId: 1),
            // HandedOff with a null target → excluded (no real assignment-to-another).
            Event(ChoreEventType.HandedOff, actorUserId: 2, targetUserId: null),
            // Non-hand-off event types are excluded by the PRIMARY Type filter even with a distinct target.
            Event(ChoreEventType.Claimed, actorUserId: 2, targetUserId: 1),
            Event(ChoreEventType.Dropped, actorUserId: 2, targetUserId: 1),
            Event(ChoreEventType.AutoReleased, actorUserId: 2, targetUserId: 1),
        };

        var result = _calc.Compute(members, Array.Empty<Chore>(), Array.Empty<Recipe>(), Array.Empty<ShoppingListItem>(), events);

        For(result, 1).HandOffs.Should().Be(1, "only the genuine hand-off-to-another counts, credited to the actor");
        For(result, 2).HandOffs.Should().Be(0, "the null-target and non-hand-off events contribute nothing");
    }

    // ---- Null-author rows are skipped (no credit for unattributed work) ------------------------------

    [Fact]
    public void Compute_NullAuthors_AreSkipped()
    {
        var members = new[] { Member(1, "Alice") };
        var recipes = new[] { Recipe(createdByUserId: null), Recipe(createdByUserId: 1) };
        var items = new[] { Item(addedByUserId: null), Item(addedByUserId: 1) };

        var result = _calc.Compute(members, Array.Empty<Chore>(), recipes, items, Array.Empty<ChoreEvent>());

        For(result, 1).RecipesAdded.Should().Be(1, "the null-author recipe is skipped");
        For(result, 1).ListItemsCurated.Should().Be(1, "the null-author list item is skipped");
    }

    // ---- Deleted recipes + auto-generated (non-manual) list items are excluded -----------------------

    [Fact]
    public void Compute_DeletedRecipes_AndNonManualItems_AreExcluded()
    {
        var members = new[] { Member(1, "Alice") };
        var recipes = new[]
        {
            Recipe(createdByUserId: 1, isDeleted: false),
            Recipe(createdByUserId: 1, isDeleted: true), // excluded
        };
        var items = new[]
        {
            Item(addedByUserId: 1, isManuallyAdded: true),
            Item(addedByUserId: 1, isManuallyAdded: false), // excluded (from meal plan, not curated)
        };

        var result = _calc.Compute(members, Array.Empty<Chore>(), recipes, items, Array.Empty<ChoreEvent>());

        For(result, 1).RecipesAdded.Should().Be(1, "the deleted recipe is excluded");
        For(result, 1).ListItemsCurated.Should().Be(1, "the non-manually-added item is excluded");
    }

    // ---- Non-member authors get no credit; only supplied members are credited ------------------------

    [Fact]
    public void Compute_OnlyCreditsSuppliedMembers()
    {
        var members = new[] { Member(1, "Alice") };
        // User 99 is not in the supplied member list — their rows are silently ignored.
        var chores = new[] { Chore(enteredByUserId: 1), Chore(enteredByUserId: 99) };

        var result = _calc.Compute(members, chores, Array.Empty<Recipe>(), Array.Empty<ShoppingListItem>(), Array.Empty<ChoreEvent>());

        result.Should().ContainSingle();
        For(result, 1).ChoresSetUp.Should().Be(1, "only the supplied member's chore is credited");
    }

    // ---- Per-member zero rows appear (mirror the equity calculator's "all members appear" rule) ------

    [Fact]
    public void Compute_MemberWithNoActivity_AppearsWithAllZeroRow()
    {
        var members = new[] { Member(1, "Alice"), Member(2, "Bob") };
        var chores = new[] { Chore(enteredByUserId: 1) };

        var result = _calc.Compute(members, chores, Array.Empty<Recipe>(), Array.Empty<ShoppingListItem>(), Array.Empty<ChoreEvent>());

        result.Should().HaveCount(2);
        var bob = For(result, 2);
        bob.ChoresSetUp.Should().Be(0);
        bob.RecipesAdded.Should().Be(0);
        bob.ListItemsCurated.Should().Be(0);
        bob.HandOffs.Should().Be(0);
    }

    // ---- Multi-member distribution across all four lanes ---------------------------------------------

    [Fact]
    public void Compute_DistributesAllFourLanes_AcrossMembers()
    {
        var members = new[] { Member(1, "Alice"), Member(2, "Bob") };

        var chores = new[]
        {
            Chore(enteredByUserId: 1), Chore(enteredByUserId: 1), Chore(enteredByUserId: 2),
        };
        var recipes = new[]
        {
            Recipe(createdByUserId: 1), Recipe(createdByUserId: 2), Recipe(createdByUserId: 2),
        };
        var items = new[]
        {
            Item(addedByUserId: 1), Item(addedByUserId: 1), Item(addedByUserId: 1), Item(addedByUserId: 2),
        };
        var events = new[]
        {
            Event(ChoreEventType.HandedOff, actorUserId: 1, targetUserId: 2),
            Event(ChoreEventType.HandedOff, actorUserId: 2, targetUserId: 1),
            Event(ChoreEventType.HandedOff, actorUserId: 2, targetUserId: 1),
        };

        var result = _calc.Compute(members, chores, recipes, items, events);

        var alice = For(result, 1);
        alice.ChoresSetUp.Should().Be(2);
        alice.RecipesAdded.Should().Be(1);
        alice.ListItemsCurated.Should().Be(3);
        alice.HandOffs.Should().Be(1);

        var bob = For(result, 2);
        bob.ChoresSetUp.Should().Be(1);
        bob.RecipesAdded.Should().Be(2);
        bob.ListItemsCurated.Should().Be(1);
        bob.HandOffs.Should().Be(2);
    }

    // ---- All-time invariance: no window, identical inputs → identical counts (V5 unit half) ----------

    [Fact]
    public void Compute_IsDeterministic_AndTakesNoWindow()
    {
        var members = new[] { Member(1, "Alice") };
        var chores = new[] { Chore(enteredByUserId: 1) };
        var recipes = new[] { Recipe(createdByUserId: 1) };
        var items = new[] { Item(addedByUserId: 1) };
        var events = new[] { Event(ChoreEventType.HandedOff, actorUserId: 1, targetUserId: 2) };

        var r1 = _calc.Compute(members, chores, recipes, items, events);
        var r2 = _calc.Compute(members, chores, recipes, items, events);

        r1.Should().BeEquivalentTo(r2, "the calculator is pure, all-time, and takes no window or clock");
    }
}
