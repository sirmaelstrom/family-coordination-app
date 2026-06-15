using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Verifies the Phase-14 checklist reset hook inside <see cref="ChoreService.CompleteAsync"/> over the EF
/// InMemory provider: on the SATISFYING completion of a recurring chore every subtask resets to
/// <c>IsDone=false</c>; a PARTIAL (non-satisfying) contribution to a multi-person chore leaves them untouched;
/// and a OneOff chore completes without error (subtasks need not reset). Subtasks NEVER gate completion, and
/// these resets must never bump <c>Chore.Version</c>.
/// </summary>
public class ChoreSubtaskResetTests : IDisposable
{
    private const int HouseholdId = 1;
    private const int Alice = 1;
    private const int Bob = 2;

    private static readonly DateTime NowBase = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _seedContext;
    private readonly FixedTimeProvider _clock = new(NowBase);
    private readonly ChoreService _service;

    public ChoreSubtaskResetTests()
    {
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _seedContext = new ApplicationDbContext(_options);

        var dbFactoryMock = new Mock<IDbContextFactory<ApplicationDbContext>>();
        dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(_options));

        _service = new ChoreService(
            dbFactoryMock.Object,
            new ChoreStatusCalculator(),
            Mock.Of<IImageService>(),
            _clock,
            new Mock<ILogger<ChoreService>>().Object);

        SeedBaseline();
    }

    private void SeedBaseline()
    {
        _seedContext.Households.Add(new Household { Id = HouseholdId, Name = "Smith" });
        _seedContext.Users.AddRange(
            new User { Id = Alice, HouseholdId = HouseholdId, Email = "a@x.com", DisplayName = "Alice" },
            new User { Id = Bob, HouseholdId = HouseholdId, Email = "b@x.com", DisplayName = "Bob" });
        _seedContext.SaveChanges();
    }

    public void Dispose()
    {
        _seedContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private static CreateChoreCommand FlexibleCmd(int requiredCount = 1) => new(
        Name: "Dishes",
        Description: null,
        RoomId: null,
        RecurrenceMode: RecurrenceMode.Flexible,
        IntervalDays: 7,
        AnchorDate: null,
        DaysOfWeek: null,
        DayOfMonth: null,
        EffortTier: EffortTier.Standard,
        OwnerUserId: null,
        AssigneeUserId: null,
        PhotoPath: null,
        Icon: "",
        RequiredCount: requiredCount);

    private static CreateChoreCommand OneOffCmd() => FlexibleCmd() with
    {
        RecurrenceMode = RecurrenceMode.OneOff,
        IntervalDays = null,
        DaysOfWeek = null,
        AnchorDate = new DateOnly(2026, 6, 1)
    };

    private async Task SeedSubtasksAsync(int choreId, params bool[] doneFlags)
    {
        await using var ctx = new ApplicationDbContext(_options);
        var order = 0;
        foreach (var done in doneFlags)
        {
            ctx.ChoreSubtasks.Add(new ChoreSubtask
            {
                HouseholdId = HouseholdId,
                ChoreId = choreId,
                Title = $"Item {order}",
                IsDone = done,
                SortOrder = order,
                CreatedAt = NowBase
            });
            order++;
        }
        await ctx.SaveChangesAsync();
    }

    private async Task<List<bool>> LoadDoneFlagsAsync(int choreId)
    {
        await using var ctx = new ApplicationDbContext(_options);
        return await ctx.ChoreSubtasks
            .Where(s => s.HouseholdId == HouseholdId && s.ChoreId == choreId)
            .OrderBy(s => s.SortOrder)
            .Select(s => s.IsDone)
            .ToListAsync();
    }

    private async Task<Chore> ReloadAsync(int choreId)
    {
        await using var ctx = new ApplicationDbContext(_options);
        return await ctx.Chores.FirstAsync(c => c.HouseholdId == HouseholdId && c.ChoreId == choreId);
    }

    // (a) Recurring chore, satisfying completion → ALL subtasks reset.

    [Theory]
    [InlineData(RecurrenceMode.Flexible)]
    [InlineData(RecurrenceMode.Fixed)]
    public async Task Complete_Recurring_Satisfying_ResetsAllSubtasks(RecurrenceMode mode)
    {
        var cmd = FlexibleCmd() with
        {
            RecurrenceMode = mode,
            IntervalDays = 7
        };
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, cmd);
        await SeedSubtasksAsync(created.ChoreId, true, true, false);

        var current = await ReloadAsync(created.ChoreId);
        await _service.CompleteAsync(HouseholdId, created.ChoreId, Alice, null, null, participantUserIds: null, current.Version);

        var flags = await LoadDoneFlagsAsync(created.ChoreId);
        flags.Should().AllBeEquivalentTo(false, "a satisfying completion of a recurring chore resets the checklist");
    }

    // (b) Multi-person chore (RequiredCount=2): first (partial) contribution leaves subtasks untouched; the
    //     satisfying second contribution resets them.

    [Fact]
    public async Task Complete_MultiPerson_PartialLeavesSubtasksUnchanged_ThenSatisfyingResets()
    {
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd(requiredCount: 2));
        await SeedSubtasksAsync(created.ChoreId, true, false, true);

        // FIRST contribution (Alice) — partial, RequiredCount=2 unmet → subtasks UNCHANGED.
        await _service.CompleteAsync(
            HouseholdId, created.ChoreId, Alice, null, null, participantUserIds: null, (await ReloadAsync(created.ChoreId)).Version);

        var afterPartial = await LoadDoneFlagsAsync(created.ChoreId);
        afterPartial.Should().Equal(new[] { true, false, true }, "a partial contribution must not reset the checklist");

        // SECOND distinct contribution (Bob) — satisfies RequiredCount=2 → reset.
        await _service.CompleteAsync(
            HouseholdId, created.ChoreId, Bob, null, null, participantUserIds: null, (await ReloadAsync(created.ChoreId)).Version);

        var afterSatisfy = await LoadDoneFlagsAsync(created.ChoreId);
        afterSatisfy.Should().AllBeEquivalentTo(false, "the satisfying contribution resets the checklist");
    }

    // (c) OneOff chore completes without error (subtasks need not reset — its lifecycle terminates).

    [Fact]
    public async Task Complete_OneOff_DoesNotError_WithSubtasks()
    {
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, OneOffCmd());
        await SeedSubtasksAsync(created.ChoreId, true, false);

        var current = await ReloadAsync(created.ChoreId);
        var act = async () => await _service.CompleteAsync(
            HouseholdId, created.ChoreId, Alice, null, null, participantUserIds: null, current.Version);

        await act.Should().NotThrowAsync();

        var result = await ReloadAsync(created.ChoreId);
        result.Status.Should().Be(ChoreStatus.Done, "a OneOff terminates on completion");
        // The OneOff branch is skipped by the reset hook — the prior done state is left as-is (no error).
        var flags = await LoadDoneFlagsAsync(created.ChoreId);
        flags.Should().Equal(new[] { true, false });
    }
}
