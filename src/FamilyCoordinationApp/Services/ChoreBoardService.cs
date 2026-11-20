using Microsoft.EntityFrameworkCore;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Dtos;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// Read/projection side of the chore board (WP-05). One short-lived <see cref="ApplicationDbContext"/> per
/// call (M2); every query is scoped to the caller's household by the "Tenant" query filter (M1; the
/// <c>householdId</c> argument is no longer read by the queries, WP-05).
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

        // Active chores only (M1: the Tenant filter scopes the household). A completed OneOff (Status=Done) or Archived chore must
        // not linger on the board or in the rollups (council) — recurring chores stay Active.
        var chores = await context.Chores
            .Where(c => c.Status == ChoreStatus.Active)
            .ToListAsync(cancellationToken);

        var rooms = await context.Rooms
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.Name)
            .ToListAsync(cancellationToken);

        var memberRows = await context.Users
            .OrderBy(u => u.DisplayName)
            .Select(u => new { Dto = new MemberDto(u.Id, u.DisplayName, u.Initials, u.PictureUrl), u.IsWhitelisted })
            .ToListAsync(cancellationToken);
        var members = memberRows.Select(r => r.Dto).ToList();
        // Who a NEW assignment may name: the same rule as HouseholdMembership.EnsureAssignableAsync (enabled members).
        // Members stays everyone, so history labels and completion credit still resolve a disabled member (59b0e3b6).
        var assignableUserIds = memberRows.Where(r => r.IsWhitelisted).Select(r => r.Dto.UserId).ToList();

        // The caller's own row carries BOTH the roaming default lens AND (Phase 15 R4′) the self-set
        // physical-capacity tier the up-for-grabs "Fits me" chip reads. Widen the existing single-user
        // projection to an anonymous type — STILL ONE query (E4), no new round-trip. A missing row (caller not
        // found) ⇒ both null, preserving the prior FirstOrDefault-on-string behavior.
        var caller = await context.Users
            .Where(u => u.Id == userId)
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

        var (completionsByChore, participationByChore) =
            await LoadRosterInputsAsync(context, multiPersonIds, cancellationToken);

        // Phase 14: load every chore's subtasks in ONE household-scoped query, ordered + grouped by ChoreId
        // (mirrors the completions/participation batch pattern). A chore with no checklist gets an empty list.
        var choreIds = chores.Select(c => c.ChoreId).ToList();
        var subtasksByChore = (await context.ChoreSubtasks
            .Where(s => choreIds.Contains(s.ChoreId))
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
            AssignableUserIds = assignableUserIds,
        };
    }

    /// <inheritdoc />
    public async Task<ChoreHomeStatsDto> GetHomeStatsAsync(
        int householdId,
        int userId,
        DateTime? now = null,
        CancellationToken cancellationToken = default)
    {
        var asOf = now ?? timeProvider.GetUtcNow().UtcDateTime;

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        // EXACTLY GetBoardAsync's active set (M1 via the Tenant filter + Status == Active), but projected down to
        // the recurrence + assignment fields the calculator calls read — no rooms, members, subtasks, or
        // ChoreRoom memberships are touched. One row per chore: room fan-out is a rollup concern only, so a
        // multi-room chore counts ONCE here, exactly like board.Chores (MN1).
        var chores = await context.Chores
            .Where(c => c.Status == ChoreStatus.Active)
            .Select(c => new
            {
                c.ChoreId,
                c.RequiredCount,
                c.RecurrenceMode,
                c.IntervalDays,
                c.AnchorDate,
                c.DaysOfWeek,
                c.DayOfMonth,
                c.LastCompletedAt,
                c.SnoozedUntil,
                c.AssignmentKind,
                c.ClaimedAt,
            })
            .ToListAsync(cancellationToken);

        // Up-for-grabs is per member for a multi-person chore (the board lens's roster rule), so fold the same
        // roster GetBoardAsync folds — loaded only when the household has a multi-person chore, as there.
        var multiPersonIds = chores.Where(c => c.RequiredCount > 1).Select(c => c.ChoreId).ToList();
        var (completionsByChore, participationByChore) =
            await LoadRosterInputsAsync(context, multiPersonIds, cancellationToken);

        // Same reduction as ChoreHomeStats.Compute over the board's ChoreDtos, applied to the same computed
        // dueness and roster inputs: a snoozed chore already reads Scheduled (auto-excluded from
        // Overdue/DueToday); UpForGrabs is ChoreHomeStats.IsUpForGrabs, the board lens's rule.
        int overdue = 0, dueToday = 0, upForGrabs = 0;
        foreach (var c in chores)
        {
            var snapshot = new ChoreRecurrenceSnapshot(
                c.RecurrenceMode, c.IntervalDays, c.AnchorDate, c.DaysOfWeek, c.DayOfMonth,
                c.LastCompletedAt, c.SnoozedUntil);
            var dueness = calculator.Compute(snapshot, asOf, timeZone);

            if (dueness.DueState == DueState.Overdue)
            {
                overdue++;
            }
            else if (dueness.DueState == DueState.DueToday)
            {
                dueToday++;
            }

            // X=1: the trio roster never decides (the multi-person arm is the only roster reader), so skip the fold.
            var roster = c.RequiredCount > 1
                ? ChoreRosterCalculator.Fold(
                    participationByChore.GetValueOrDefault(c.ChoreId) ?? [],
                    completionsByChore.GetValueOrDefault(c.ChoreId) ?? [],
                    c.LastCompletedAt,
                    c.RequiredCount,
                    recurring: c.RecurrenceMode != RecurrenceMode.OneOff)
                : null;

            if (ChoreHomeStats.IsUpForGrabs(
                    dueness.IsSnoozed,
                    c.RequiredCount,
                    roster?.CompletedCount ?? 0,
                    roster?.Members.Select(m => m.UserId) ?? [],
                    c.AssignmentKind,
                    calculator.IsClaimStale(c.AssignmentKind, c.ClaimedAt, asOf),
                    userId))
            {
                upForGrabs++;
            }
        }

        return new ChoreHomeStatsDto(chores.Count, overdue, dueToday, upForGrabs);
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

    // ------------------------------------------------------------------ roster inputs

    /// <summary>
    /// The completion ledger and participation events of the given multi-person chores, each grouped by
    /// <c>ChoreId</c> — the inputs <see cref="ChoreRosterCalculator.Fold"/> derives a roster from. P5: no query
    /// at all when there are no multi-person chores; otherwise one household-scoped query each (M7).
    /// </summary>
    private static async Task<(Dictionary<int, List<ChoreCompletion>> Completions,
        Dictionary<int, List<ChoreParticipationEvent>> Participation)> LoadRosterInputsAsync(
        ApplicationDbContext context,
        IReadOnlyList<int> multiPersonIds,
        CancellationToken cancellationToken)
    {
        if (multiPersonIds.Count == 0)
        {
            return ([], []);
        }

        var completions = (await context.ChoreCompletions
                .Where(c => multiPersonIds.Contains(c.ChoreId))
                .ToListAsync(cancellationToken))
            .GroupBy(c => c.ChoreId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var participation = (await context.ChoreParticipationEvents
                .Where(e => multiPersonIds.Contains(e.ChoreId))
                .ToListAsync(cancellationToken))
            .GroupBy(e => e.ChoreId)
            .ToDictionary(g => g.Key, g => g.ToList());

        return (completions, participation);
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
