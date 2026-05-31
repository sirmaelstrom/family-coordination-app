using Microsoft.EntityFrameworkCore;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Dtos;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// Read/projection side of the chore board (WP-05). One short-lived <see cref="ApplicationDbContext"/> per
/// call (M2); every query is filtered by the caller-supplied <c>householdId</c> (M1, never client-supplied).
/// All dueness/freshness/staleness math is delegated to <see cref="ChoreStatusCalculator"/> (WP-02) using
/// the injected <see cref="TimeZoneInfo"/> — there is no <see cref="DateTime.UtcNow"/> reached inside and no
/// raw date-string parse (D14/M5). Read-only: no writes, no transitions (WP-04).
/// </summary>
public class ChoreBoardService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ChoreStatusCalculator calculator,
    TimeZoneInfo timeZone,
    TimeProvider timeProvider) : IChoreBoardService
{
    public async Task<ChoreBoardDto> GetBoardAsync(
        int householdId,
        int userId,
        DateTime? now = null,
        CancellationToken cancellationToken = default)
    {
        var asOf = now ?? timeProvider.GetUtcNow().UtcDateTime;

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Active chores only (M1 household filter). A completed OneOff (Status=Done) or Archived chore must
        // not linger on the board or in the rollups (council) — recurring chores stay Active.
        var chores = await context.Chores
            .Where(c => c.HouseholdId == householdId && c.Status == ChoreStatus.Active)
            .ToListAsync(cancellationToken);

        var rooms = await context.Rooms
            .Where(r => r.HouseholdId == householdId)
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.Name)
            .ToListAsync(cancellationToken);

        var members = await context.Users
            .Where(u => u.HouseholdId == householdId)
            .OrderBy(u => u.DisplayName)
            .Select(u => new MemberDto(u.Id, u.DisplayName, u.Initials, u.PictureUrl))
            .ToListAsync(cancellationToken);

        var userDefaultView = await context.Users
            .Where(u => u.HouseholdId == householdId && u.Id == userId)
            .Select(u => u.ChoresDefaultView)
            .FirstOrDefaultAsync(cancellationToken);

        // Project each chore once; reuse the computed dueness for both the per-chore DTO and the rollups so
        // a chore's dueness is computed in exactly one place (no divergence between the card and the room
        // bucket). The board buckets off COMPUTED dueness, never stored Status (decay would be ignored).
        var projected = chores
            .Select(chore => new
            {
                Chore = chore,
                Dueness = calculator.Compute(ChoreRecurrenceSnapshot.FromChore(chore), asOf, timeZone),
                IsClaimStale = calculator.IsClaimStale(chore.AssignmentKind, chore.ClaimedAt, asOf)
            })
            .ToList();

        var choreDtos = projected
            .Select(p => ToDto(p.Chore, p.Dueness, p.IsClaimStale))
            .ToList();

        var rollups = BuildRollups(projected.Select(p => (p.Chore, p.Dueness)), rooms);

        var needsAttentionIds = projected
            .Where(p => IsNeedsAttention(p.Chore, p.Dueness))
            .OrderBy(p => DuenessRank(p.Dueness.DueState))                  // dirtiest first (overdue→due→…)
            .ThenBy(p => p.Dueness.NextDueAt ?? DateTime.MaxValue)          // most overdue / soonest due first
            .ThenBy(p => p.Chore.ChoreId)                                   // stable tie-break
            .Select(p => p.Chore.ChoreId)
            .ToList();

        return new ChoreBoardDto(
            choreDtos,
            rollups,
            members,
            needsAttentionIds,
            userDefaultView);
    }

    /// <inheritdoc />
    public ChoreDto ProjectChore(Chore chore, DateTime now, TimeZoneInfo timeZone)
    {
        var dueness = calculator.Compute(ChoreRecurrenceSnapshot.FromChore(chore), now, timeZone);
        var isClaimStale = calculator.IsClaimStale(chore.AssignmentKind, chore.ClaimedAt, now);
        return ToDto(chore, dueness, isClaimStale);
    }

    // ------------------------------------------------------------------ projection

    private static ChoreDto ToDto(Chore chore, ChoreDuenessResult dueness, bool isClaimStale) => new(
        chore.ChoreId,
        chore.Name,
        chore.Icon,
        chore.Description,
        chore.RoomId,
        chore.RecurrenceMode.ToString(),
        chore.IntervalDays,
        chore.DaysOfWeek,
        chore.AnchorDate,
        dueness.DueState,
        dueness.ColorTier,
        dueness.NextDueAt,
        isClaimStale,
        chore.EffortTier.ToString(),
        chore.EffortPoints,
        chore.OwnerUserId,
        chore.AssigneeUserId,
        chore.AssignmentKind,
        chore.ClaimedAt,
        chore.LastCompletedAt,
        chore.PhotoPath,
        chore.Version);

    // ------------------------------------------------------------------ rollups

    private static List<RoomRollupDto> BuildRollups(
        IEnumerable<(Chore Chore, ChoreDuenessResult Dueness)> projected,
        IReadOnlyList<Room> rooms)
    {
        // Group projected chores by RoomId. Note: a Dictionary cannot key on a null int?, so the roomless
        // bucket (RoomId == null => the virtual General group) is partitioned out separately.
        var byRoom = projected
            .Where(p => p.Chore.RoomId is not null)
            .GroupBy(p => p.Chore.RoomId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var generalChores = projected
            .Where(p => p.Chore.RoomId is null)
            .ToList();

        var rollups = new List<RoomRollupDto>();

        // Real rooms first, in their stored sort order. A room with no chores still appears (clean, 0/0).
        foreach (var room in rooms)
        {
            byRoom.TryGetValue(room.RoomId, out var roomChores);
            rollups.Add(BuildRollup(
                room.RoomId,
                room.Name,
                room.Icon,
                room.PhotoPath,
                room.SortOrder,
                roomChores));
        }

        // Virtual General group: only emitted when there are roomless chores (no Room row is created, D9).
        if (generalChores.Count > 0)
        {
            rollups.Add(BuildRollup(
                roomId: null,
                ChoreRollup.GeneralGroupName,
                ChoreRollup.GeneralGroupIcon,
                photoPath: null,
                sortOrder: int.MaxValue,   // General sorts last
                generalChores));
        }

        return rollups;
    }

    private static RoomRollupDto BuildRollup(
        int? roomId,
        string name,
        string icon,
        string? photoPath,
        int sortOrder,
        List<(Chore Chore, ChoreDuenessResult Dueness)>? chores)
    {
        var list = chores ?? [];
        var dueCount = list.Count(c => IsDueOrOverdue(c.Dueness.DueState));
        return new RoomRollupDto(
            roomId,
            name,
            icon,
            photoPath,
            sortOrder,
            list.Count,
            dueCount,
            ChoreRollup.BucketFor(dueCount));
    }

    // ------------------------------------------------------------------ needs-attention selection

    // Needs-attention = anything carrying dueness pressure (due today or overdue) PLUS unclaimed pile chores
    // (AssignmentKind.None) — the "someone should grab this" set (V6). Ordered dirtiest-first (D17).
    private static bool IsNeedsAttention(Chore chore, ChoreDuenessResult dueness) =>
        IsDueOrOverdue(dueness.DueState) || chore.AssignmentKind == AssignmentKind.None;

    private static bool IsDueOrOverdue(DueState state) =>
        state is DueState.Overdue or DueState.DueToday;

    // Lower rank sorts first: Overdue (0) → DueToday (1) → Scheduled (2) → NotDue (3).
    private static int DuenessRank(DueState state) => state switch
    {
        DueState.Overdue => 0,
        DueState.DueToday => 1,
        DueState.Scheduled => 2,
        _ => 3
    };
}
