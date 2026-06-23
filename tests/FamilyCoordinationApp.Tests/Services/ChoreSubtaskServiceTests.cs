using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Exercises the Phase-14 checklist CRUD (<see cref="ChoreSubtaskService"/>) over the EF InMemory provider:
/// the create happy path + validation (blank/too-long title, 50-item cap), update (toggle done / rename),
/// delete, sort-order append semantics, and cross-household isolation (a chore in another household 404s).
/// Versionless / last-write-wins — there is no concurrency token here.
/// </summary>
public class ChoreSubtaskServiceTests : IDisposable
{
    private const int HouseholdId = 1;
    private const int OtherHouseholdId = 2;
    private const int Alice = 1;
    private const int Bob = 2;

    private static readonly DateTime NowBase = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _seedContext;
    private readonly FixedTimeProvider _clock = new(NowBase);
    private readonly ChoreSubtaskService _service;

    private int _choreInHouseholdId;
    private int _choreInOtherHouseholdId;

    public ChoreSubtaskServiceTests()
    {
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _seedContext = new ApplicationDbContext(_options);

        var dbFactoryMock = new Mock<IDbContextFactory<ApplicationDbContext>>();
        dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(_options));

        _service = new ChoreSubtaskService(dbFactoryMock.Object, _clock);

        SeedBaseline();
    }

    private void SeedBaseline()
    {
        _seedContext.Households.AddRange(
            new Household { Id = HouseholdId, Name = "Smith" },
            new Household { Id = OtherHouseholdId, Name = "Jones" });

        var choreHere = new Chore
        {
            HouseholdId = HouseholdId,
            ChoreId = 1,
            Name = "Dishes",
            RecurrenceMode = RecurrenceMode.Flexible,
            IntervalDays = 7,
            EffortTier = EffortTier.Standard,
            EffortPoints = 2,
            Status = ChoreStatus.Active,
            EnteredByUserId = Alice,
            AssignmentKind = AssignmentKind.None,
            CreatedAt = NowBase
        };
        // Distinct ChoreId (7) so a "create on this id from HouseholdId" attempt cannot accidentally match a
        // same-numbered chore in HouseholdId — the only chore numbered 7 lives in the OTHER household.
        var choreOther = new Chore
        {
            HouseholdId = OtherHouseholdId,
            ChoreId = 7,
            Name = "Other household chore",
            RecurrenceMode = RecurrenceMode.Flexible,
            IntervalDays = 7,
            EffortTier = EffortTier.Standard,
            EffortPoints = 2,
            Status = ChoreStatus.Active,
            EnteredByUserId = Alice,
            AssignmentKind = AssignmentKind.None,
            CreatedAt = NowBase
        };
        _seedContext.Chores.AddRange(choreHere, choreOther);
        _seedContext.SaveChanges();

        _choreInHouseholdId = choreHere.ChoreId;
        _choreInOtherHouseholdId = choreOther.ChoreId;
    }

    public void Dispose()
    {
        _seedContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<ChoreSubtask?> ReloadAsync(int subtaskId)
    {
        await using var ctx = new ApplicationDbContext(_options);
        return await ctx.ChoreSubtasks
            .FirstOrDefaultAsync(s => s.HouseholdId == HouseholdId && s.ChoreId == _choreInHouseholdId && s.SubtaskId == subtaskId);
    }

    // ---- CREATE -------------------------------------------------------------

    [Fact]
    public async Task Create_HappyPath_ReturnsId_IsDoneFalse_SortZero()
    {
        var dto = await _service.CreateAsync(HouseholdId, _choreInHouseholdId, "Wipe counter");

        dto.Id.Should().BeGreaterThan(0, "SubtaskId is DB-assigned");
        dto.Title.Should().Be("Wipe counter");
        dto.IsDone.Should().BeFalse();
        dto.SortOrder.Should().Be(0, "the first subtask appends at sort order 0");

        var persisted = await ReloadAsync(dto.Id);
        persisted.Should().NotBeNull();
        persisted!.CreatedAt.Should().Be(NowBase);
    }

    [Fact]
    public async Task Create_TrimsTitle()
    {
        var dto = await _service.CreateAsync(HouseholdId, _choreInHouseholdId, "  Wipe counter  ");
        dto.Title.Should().Be("Wipe counter");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_BlankTitle_Throws(string title)
    {
        var act = async () => await _service.CreateAsync(HouseholdId, _choreInHouseholdId, title);
        await act.Should().ThrowAsync<ChoreValidationException>();
    }

    [Fact]
    public async Task Create_TitleOver200_Throws()
    {
        var tooLong = new string('x', 201);
        var act = async () => await _service.CreateAsync(HouseholdId, _choreInHouseholdId, tooLong);
        await act.Should().ThrowAsync<ChoreValidationException>();
    }

    [Fact]
    public async Task Create_FiftyCap_FiftyFirstThrows()
    {
        for (var i = 0; i < 50; i++)
        {
            await _service.CreateAsync(HouseholdId, _choreInHouseholdId, $"Item {i}");
        }

        var act = async () => await _service.CreateAsync(HouseholdId, _choreInHouseholdId, "Item 51");
        await act.Should().ThrowAsync<ChoreValidationException>()
            .WithMessage("*50*");
    }

    [Fact]
    public async Task Create_SortOrder_IncrementsAcrossCreates()
    {
        var first = await _service.CreateAsync(HouseholdId, _choreInHouseholdId, "First");
        var second = await _service.CreateAsync(HouseholdId, _choreInHouseholdId, "Second");
        var third = await _service.CreateAsync(HouseholdId, _choreInHouseholdId, "Third");

        first.SortOrder.Should().Be(0);
        second.SortOrder.Should().Be(1);
        third.SortOrder.Should().Be(2);
    }

    [Fact]
    public async Task Create_CrossHouseholdChore_ThrowsNotFound()
    {
        // The chore (id 7) exists only in OtherHouseholdId. A HouseholdId caller targeting it finds nothing via
        // the (householdId, choreId) filter → 404 (cross-household isolation, M1).
        var act = async () => await _service.CreateAsync(HouseholdId, _choreInOtherHouseholdId, "X");
        await act.Should().ThrowAsync<ChoreNotFoundException>();

        // Sanity: the same chore IS reachable from its own household (proving it's the householdId filter, not a
        // bad chore id, that blocks the cross-household attempt above).
        var allowed = await _service.CreateAsync(OtherHouseholdId, _choreInOtherHouseholdId, "Allowed in its own household");
        allowed.Id.Should().BeGreaterThan(0);
    }

    // ---- UPDATE -------------------------------------------------------------

    [Fact]
    public async Task Update_TogglesIsDone()
    {
        var created = await _service.CreateAsync(HouseholdId, _choreInHouseholdId, "Toggle me");

        var updated = await _service.UpdateAsync(HouseholdId, _choreInHouseholdId, created.Id, Alice, title: null, isDone: true, sortOrder: null);
        updated.IsDone.Should().BeTrue();
        (await ReloadAsync(created.Id))!.IsDone.Should().BeTrue();

        var back = await _service.UpdateAsync(HouseholdId, _choreInHouseholdId, created.Id, Alice, title: null, isDone: false, sortOrder: null);
        back.IsDone.Should().BeFalse();
    }

    [Fact]
    public async Task Update_Ticking_CapturesActor_AndUnticking_ClearsIt()
    {
        var created = await _service.CreateAsync(HouseholdId, _choreInHouseholdId, "Who did it");

        // false -> true: stamp the acting user + UtcNow.
        var ticked = await _service.UpdateAsync(HouseholdId, _choreInHouseholdId, created.Id, Alice, title: null, isDone: true, sortOrder: null);
        ticked.CompletedByUserId.Should().Be(Alice);
        ticked.CompletedAt.Should().Be(NowBase);
        var afterTick = await ReloadAsync(created.Id);
        afterTick!.CompletedByUserId.Should().Be(Alice);
        afterTick.CompletedAt.Should().Be(NowBase);

        // -> false: clear the actor (per-occurrence invariant: actor set IFF IsDone).
        var unticked = await _service.UpdateAsync(HouseholdId, _choreInHouseholdId, created.Id, Alice, title: null, isDone: false, sortOrder: null);
        unticked.CompletedByUserId.Should().BeNull();
        unticked.CompletedAt.Should().BeNull();
        var afterUntick = await ReloadAsync(created.Id);
        afterUntick!.CompletedByUserId.Should().BeNull();
        afterUntick.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task Update_RetickingAlreadyDone_PreservesOriginalActor()
    {
        var created = await _service.CreateAsync(HouseholdId, _choreInHouseholdId, "Stable actor");
        await _service.UpdateAsync(HouseholdId, _choreInHouseholdId, created.Id, Alice, title: null, isDone: true, sortOrder: null);

        // A second isDone:true write by a different user must NOT re-stamp (true->true is a no-op for the actor).
        var again = await _service.UpdateAsync(HouseholdId, _choreInHouseholdId, created.Id, Bob, title: null, isDone: true, sortOrder: null);
        again.CompletedByUserId.Should().Be(Alice, "re-ticking an already-done item preserves the original actor");
        again.CompletedAt.Should().Be(NowBase);
    }

    [Fact]
    public async Task Update_RenameOnly_DoesNotTouchActor()
    {
        var created = await _service.CreateAsync(HouseholdId, _choreInHouseholdId, "Done item");
        await _service.UpdateAsync(HouseholdId, _choreInHouseholdId, created.Id, Alice, title: null, isDone: true, sortOrder: null);

        // A title-only update (isDone == null) leaves the actor stamp intact.
        var renamed = await _service.UpdateAsync(HouseholdId, _choreInHouseholdId, created.Id, Bob, title: "Renamed", isDone: null, sortOrder: null);
        renamed.CompletedByUserId.Should().Be(Alice);
        renamed.CompletedAt.Should().Be(NowBase);
    }

    [Fact]
    public async Task Update_Renames()
    {
        var created = await _service.CreateAsync(HouseholdId, _choreInHouseholdId, "Old name");

        var updated = await _service.UpdateAsync(HouseholdId, _choreInHouseholdId, created.Id, Alice, title: "  New name  ", isDone: null, sortOrder: null);
        updated.Title.Should().Be("New name", "title is trimmed on update");
        (await ReloadAsync(created.Id))!.Title.Should().Be("New name");
    }

    [Fact]
    public async Task Update_BlankTitle_Throws()
    {
        var created = await _service.CreateAsync(HouseholdId, _choreInHouseholdId, "Valid");
        var act = async () => await _service.UpdateAsync(HouseholdId, _choreInHouseholdId, created.Id, Alice, title: "   ", isDone: null, sortOrder: null);
        await act.Should().ThrowAsync<ChoreValidationException>();
    }

    [Fact]
    public async Task Update_OnlyAppliesNonNullFields()
    {
        var created = await _service.CreateAsync(HouseholdId, _choreInHouseholdId, "Keep title");
        await _service.UpdateAsync(HouseholdId, _choreInHouseholdId, created.Id, Alice, title: null, isDone: true, sortOrder: 5);

        var reloaded = await ReloadAsync(created.Id);
        reloaded!.Title.Should().Be("Keep title", "a null title leaves the field unchanged");
        reloaded.IsDone.Should().BeTrue();
        reloaded.SortOrder.Should().Be(5);
    }

    [Fact]
    public async Task Update_MissingSubtask_ThrowsNotFound()
    {
        var act = async () => await _service.UpdateAsync(HouseholdId, _choreInHouseholdId, subtaskId: 9999, actingUserId: Alice, title: "X", isDone: null, sortOrder: null);
        await act.Should().ThrowAsync<ChoreNotFoundException>();
    }

    // ---- REORDER ------------------------------------------------------------

    [Fact]
    public async Task Reorder_ReassignsSortOrderByPosition()
    {
        var a = await _service.CreateAsync(HouseholdId, _choreInHouseholdId, "A"); // sortOrder 0
        var b = await _service.CreateAsync(HouseholdId, _choreInHouseholdId, "B"); // sortOrder 1
        var c = await _service.CreateAsync(HouseholdId, _choreInHouseholdId, "C"); // sortOrder 2

        // New order: C, A, B.
        await _service.ReorderAsync(HouseholdId, _choreInHouseholdId, new[] { c.Id, a.Id, b.Id });

        (await ReloadAsync(c.Id))!.SortOrder.Should().Be(0);
        (await ReloadAsync(a.Id))!.SortOrder.Should().Be(1);
        (await ReloadAsync(b.Id))!.SortOrder.Should().Be(2);
    }

    [Fact]
    public async Task Reorder_IgnoresForeignIds()
    {
        var a = await _service.CreateAsync(HouseholdId, _choreInHouseholdId, "A");
        var b = await _service.CreateAsync(HouseholdId, _choreInHouseholdId, "B");

        // 9999 is not a subtask of this chore — it is skipped, and the present ids re-index by position.
        var act = async () => await _service.ReorderAsync(HouseholdId, _choreInHouseholdId, new[] { b.Id, 9999, a.Id });
        await act.Should().NotThrowAsync();

        (await ReloadAsync(b.Id))!.SortOrder.Should().Be(0);
        (await ReloadAsync(a.Id))!.SortOrder.Should().Be(2);
    }

    // ---- DELETE -------------------------------------------------------------

    [Fact]
    public async Task Delete_RemovesSubtask()
    {
        var created = await _service.CreateAsync(HouseholdId, _choreInHouseholdId, "Delete me");

        await _service.DeleteAsync(HouseholdId, _choreInHouseholdId, created.Id);

        (await ReloadAsync(created.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Delete_MissingSubtask_ThrowsNotFound()
    {
        var act = async () => await _service.DeleteAsync(HouseholdId, _choreInHouseholdId, subtaskId: 9999);
        await act.Should().ThrowAsync<ChoreNotFoundException>();
    }
}
