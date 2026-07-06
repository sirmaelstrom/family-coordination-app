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

        // The caller's own row carries BOTH the roaming default lens AND (Phase 15 R4′) the self-set
        // physical-capacity tier the up-for-grabs "Fits me" chip reads. Widen the existing single-user
        // projection to an anonymous type — STILL ONE query (E4), no new round-trip. A missing row (caller not
        // found) ⇒ both null, preserving the prior FirstOrDefault-on-string behavior.
        var caller = await context.Users
            .Where(u => u.HouseholdId == householdId && u.Id == userId)
            .Select(u => new { u.ChoresDefaultView, u.PhysicalCapacityTier })
            .FirstOrDefaultAsync(cancellationToken);
        var userDefaultView = caller?.ChoresDefaultView;
        var callerCapacityTier = caller?.PhysicalCapacityTier;

        // P5: Lazy progress query — only issued when the household has at least one multi-person chore.
        // Load completions in ONE query, group by ChoreId; single-person chores get an empty set.
        var multiPersonIds = chores
            .Where(c => c.RequiredCount > 1)
            .Select(c => c.ChoreId)
            .ToList();

        Dictionary<int, List<ChoreCompletion>> completionsByChore = [];
        if (multiPersonIds.Count > 0)
        {
            // M7: single household-scoped query covering only the multi-person chore ids.
            var allCompletions = await context.ChoreCompletions
                .Where(c => c.HouseholdId == householdId && multiPersonIds.Contains(c.ChoreId))
                .ToListAsync(cancellationToken);

            completionsByChore = allCompletions
                .GroupBy(c => c.ChoreId)
                .ToDictionary(g => g.Key, g => g.ToList());
        }

        // P5 (rework): lazily load participation events for the same multi-person chore set — the fold
        // (ChoreRosterCalculator) derives the named roster on read. Single-person chores never query this.
        Dictionary<int, List<ChoreParticipationEvent>> participationByChore = [];
        if (multiPersonIds.Count > 0)
        {
            var allEvents = await context.ChoreParticipationEvents
                .Where(e => e.HouseholdId == householdId && multiPersonIds.Contains(e.ChoreId))
                .ToListAsync(cancellationToken);

            participationByChore = allEvents
                .GroupBy(e => e.ChoreId)
                .ToDictionary(g => g.Key, g => g.ToList());
        }

        // Phase 14: load every chore's subtasks in ONE household-scoped query, ordered + grouped by ChoreId
        // (mirrors the completions/participation batch pattern). A chore with no checklist gets an empty list.
        var choreIds = chores.Select(c => c.ChoreId).ToList();
        var subtasksByChore = (await context.ChoreSubtasks
            .Where(s => s.HouseholdId == householdId && choreIds.Contains(s.ChoreId))
            .ToListAsync(cancellationToken))
            .GroupBy(s => s.ChoreId)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.SortOrder).ThenBy(s => s.SubtaskId)
                .Select(s => new ChoreSubtaskDto(s.SubtaskId, s.Title, s.IsDone, s.SortOrder, s.CompletedByUserId, s.CompletedAt)).ToList());

        // Phase 13: every chore's room memberships in ONE household-scoped query (M1 — no N+1). choreId → sorted
        // roomIds; a chore with no rooms is absent (→ empty list = General). Threaded into both the per-chore
        // DTO (RoomIds) and the rollup fan-out.
        var membershipsByChore = await ChoreRoomMembership.LoadMembershipsAsync(context, householdId, cancellationToken);

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
            .Select(p =>
            {
                // X>1: fold the named roster (events + completion ledger) for the current occurrence.
                // X=1: synthesize a 0/1-member roster from the assignment trio (CompletedCount stays 0).
                IReadOnlyList<RosterMemberDto> roster;
                int completedCount;
                if (p.Chore.RequiredCount > 1)
                {
                    var derived = ChoreRosterCalculator.Fold(
                        participationByChore.GetValueOrDefault(p.Chore.ChoreId) ?? [],
                        completionsByChore.GetValueOrDefault(p.Chore.ChoreId) ?? [],
                        p.Chore.LastCompletedAt,
                        p.Chore.RequiredCount,
                        recurring: p.Chore.RecurrenceMode != RecurrenceMode.OneOff);
                    roster = derived.Members;
                    completedCount = derived.CompletedCount;
                }
                else
                {
                    roster = SynthesizeTrioRoster(p.Chore);
                    completedCount = 0;
                }
                var subtasks = subtasksByChore.GetValueOrDefault(p.Chore.ChoreId) ?? [];
                var roomIds = membershipsByChore.GetValueOrDefault(p.Chore.ChoreId) ?? [];
                return ToDto(p.Chore, p.Dueness, p.IsClaimStale, roomIds, roster, completedCount, subtasks);
            })
            .ToList();

        var rollups = BuildRollups(projected.Select(p => (p.Chore, p.Dueness)), membershipsByChore, rooms);

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
            userDefaultView)
        {
            CallerCapacityTier = callerCapacityTier,
        };
    }

    /// <inheritdoc />
    public ChoreDto ProjectChore(Chore chore, DateTime now, TimeZoneInfo timeZone, IReadOnlyList<int> roomIds)
    {
        var dueness = calculator.Compute(ChoreRecurrenceSnapshot.FromChore(chore), now, timeZone);
        var isClaimStale = calculator.IsClaimStale(chore.AssignmentKind, chore.ClaimedAt, now);
        // The roster is folded only in GetBoardAsync (it needs the participation events + completion ledger).
        // The single-chore mutation response synthesizes the X=1 trio roster and returns an EMPTY roster for
        // X>1 with completedCount=0 — the island reconciles via the board GET for authoritative progress (WP-07).
        var roster = chore.RequiredCount > 1 ? Array.Empty<RosterMemberDto>() : SynthesizeTrioRoster(chore);
        // Map the chore's own subtasks (ordered). Accessing an unloaded EF collection returns the empty
        // initialized list (no lazy query) — so this is empty unless the caller .Include'd them (ChoreService
        // mutations do, so the post-reset unchecked checklist is reflected without a board refetch).
        var subtasks = (chore.Subtasks ?? [])
            .OrderBy(s => s.SortOrder).ThenBy(s => s.SubtaskId)
            .Select(s => new ChoreSubtaskDto(s.SubtaskId, s.Title, s.IsDone, s.SortOrder, s.CompletedByUserId, s.CompletedAt)).ToList();
        return ToDto(chore, dueness, isClaimStale, roomIds, roster, completedCount: 0, subtasks);
    }

    // ------------------------------------------------------------------ projection

    private static ChoreDto ToDto(
        Chore chore,
        ChoreDuenessResult dueness,
        bool isClaimStale,
        IReadOnlyList<int> roomIds,
        IReadOnlyList<RosterMemberDto> roster,
        int completedCount,
        IReadOnlyList<ChoreSubtaskDto> subtasks) => new(
        chore.ChoreId,
        chore.Name,
        chore.Icon,
        chore.Description,
        roomIds,
        chore.RecurrenceMode.ToString(),
        chore.IntervalDays,
        chore.DaysOfWeek,
        chore.AnchorDate,
        dueness.DueState,
        dueness.ColorTier,
        dueness.NextDueAt,
        chore.SnoozedUntil,
        dueness.IsSnoozed,   // server-computed gate (today < SnoozedUntil), NOT just "column set"
        isClaimStale,
        chore.EffortTier.ToString(),
        chore.EffortPoints,
        chore.OwnerUserId,
        chore.AssigneeUserId,
        chore.AssignmentKind,
        chore.ClaimedAt,
        chore.LastCompletedAt,
        chore.PhotoPath,
        chore.Version,
        chore.RequiredCount,
        completedCount,
        roster,
        subtasks);

    /// <summary>
    /// Synthesize the uniform <c>roster[]</c> for a single-person (X=1) chore from the assignment trio (D3),
    /// so the card can render it identically to a multi-person chore. The trio holder becomes one member:
    /// <see cref="RosterState.In"/> when self-<see cref="AssignmentKind.Claimed"/>,
    /// <see cref="RosterState.Assigned"/> when deliberately <see cref="AssignmentKind.Assigned"/>. Unassigned
    /// ⇒ empty. (X=1 advances on a single completion, so a "done" state is transient and not surfaced here.)
    /// </summary>
    private static IReadOnlyList<RosterMemberDto> SynthesizeTrioRoster(Chore chore)
    {
        if (chore.AssigneeUserId is int assignee && chore.AssignmentKind != AssignmentKind.None)
        {
            var state = chore.AssignmentKind == AssignmentKind.Claimed ? RosterState.In : RosterState.Assigned;
            return [new RosterMemberDto(assignee, state)];
        }

        return [];
    }

    // ------------------------------------------------------------------ rollups

    private static List<RoomRollupDto> BuildRollups(
        IEnumerable<(Chore Chore, ChoreDuenessResult Dueness)> projected,
        IReadOnlyDictionary<int, IReadOnlyList<int>> membershipsByChore,
        IReadOnlyList<Room> rooms)
    {
        // Fan out per membership (Phase 13): a chore counts in EACH of its member rooms — a 2-room chore
        // appears in both rooms' rollups. A chore with zero memberships is the virtual General group. The flat
        // board.chores list stays one-per-chore (M3), so household aggregates that read it never double-count
        // (MN1) — only the rollups fan out here.
        var byRoom = new Dictionary<int, List<(Chore Chore, ChoreDuenessResult Dueness)>>();
        var generalChores = new List<(Chore Chore, ChoreDuenessResult Dueness)>();

        foreach (var p in projected)
        {
            var roomIds = membershipsByChore.GetValueOrDefault(p.Chore.ChoreId) ?? [];
            if (roomIds.Count == 0)
            {
                generalChores.Add(p);
                continue;
            }

            foreach (var roomId in roomIds)
            {
                if (!byRoom.TryGetValue(roomId, out var list))
                {
                    list = [];
                    byRoom[roomId] = list;
                }
                list.Add(p);
            }
        }

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
    // (AssignmentKind.None) — the "someone should grab this" set (V6). Ordered dirtiest-first (D17). A snoozed
    // chore is excluded outright: it already reads Scheduled (so the due/overdue arm is moot), and the
    // !IsSnoozed gate also drops it from the unclaimed-pile arm — closing the snoozed-unclaimed leak (WP-04).
    private static bool IsNeedsAttention(Chore chore, ChoreDuenessResult dueness) =>
        !dueness.IsSnoozed && (IsDueOrOverdue(dueness.DueState) || chore.AssignmentKind == AssignmentKind.None);

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
