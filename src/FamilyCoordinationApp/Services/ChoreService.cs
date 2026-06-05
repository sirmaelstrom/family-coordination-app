using Microsoft.EntityFrameworkCore;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// Writes + the claim state machine for chores (WP-04). One short-lived <see cref="ApplicationDbContext"/>
/// per op (M2); every query/mutation is filtered by the caller-supplied <c>householdId</c> (M1, never
/// client-supplied). Timestamps are written as UTC <see cref="DateTime"/> (Kind=Utc, M10) from the injected
/// <see cref="TimeProvider"/>. Staleness math is delegated to <see cref="ChoreStatusCalculator"/> (WP-02) —
/// this service computes no decay colors of its own. No <c>BackgroundService</c>: auto-release is lazy
/// compute + materialize, triggered on the next mutation that touches a chore (MN1/D7).
/// <para><b>Recurrence advance on completion is derived, not stored</b> (per the WP): there is no stored
/// <c>NextDueAt</c> column — completion only sets <c>LastCompletedAt</c> (and Status=Done for OneOff). The
/// next-due derivation lives in <see cref="ChoreStatusCalculator"/> and runs on read (WP-05), so this
/// service needs no <see cref="TimeZoneInfo"/>: UTC storage + tz-agnostic staleness only.</para>
/// </summary>
public class ChoreService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ChoreStatusCalculator calculator,
    IImageService imageService,
    TimeProvider timeProvider,
    ILogger<ChoreService> logger) : IChoreService
{
    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    // ------------------------------------------------------------------ CRUD

    public async Task<Chore> CreateChoreAsync(int householdId, int actorUserId, CreateChoreCommand cmd, CancellationToken cancellationToken = default)
    {
        ValidateName(cmd.Name);
        ValidateRecurrence(cmd.RecurrenceMode, cmd.IntervalDays, cmd.DaysOfWeek, cmd.DayOfMonth);
        ValidateRequiredCount(cmd.RequiredCount);

        var now = UtcNow();

        return await IdGenerationHelper.ExecuteWithRetryAsync(
            async (attempt) =>
            {
                await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

                // A non-null assignee at creation must be a member (M1). X=1 only — a multi-person chore
                // (RequiredCount>1) ignores AssigneeUserId and seeds its named roster from AssignedUserIds.
                if (cmd.RequiredCount == 1 && cmd.AssigneeUserId is { } assigneeId)
                {
                    await EnsureHouseholdMemberAsync(context, householdId, assigneeId, cancellationToken);
                }

                var maxId = await context.Chores
                    .Where(c => c.HouseholdId == householdId)
                    .MaxAsync(c => (int?)c.ChoreId, cancellationToken) ?? 0;

                var chore = new Chore
                {
                    HouseholdId = householdId,
                    ChoreId = maxId + 1,
                    Name = cmd.Name.Trim(),
                    Description = string.IsNullOrWhiteSpace(cmd.Description) ? null : cmd.Description.Trim(),
                    Icon = cmd.Icon ?? string.Empty,
                    RoomId = cmd.RoomId,
                    RecurrenceMode = cmd.RecurrenceMode,
                    IntervalDays = cmd.IntervalDays,
                    AnchorDate = cmd.AnchorDate,
                    DaysOfWeek = cmd.DaysOfWeek,
                    DayOfMonth = cmd.DayOfMonth,
                    EffortTier = cmd.EffortTier,
                    EffortPoints = ChoreEffort.PointsFor(cmd.EffortTier),
                    RequiredCount = cmd.RequiredCount,
                    Status = ChoreStatus.Active,
                    EnteredByUserId = actorUserId,
                    OwnerUserId = cmd.OwnerUserId,
                    PhotoPath = cmd.PhotoPath,
                    CreatedAt = now,
                    LastCompletedAt = null
                };

                // Assignment: X=1 uses the single-holder trio (today's behavior); X>1 leaves the trio on the
                // pile and seeds its named roster from AssignedUserIds via participation events (D3/D8).
                if (chore.RequiredCount == 1 && cmd.AssigneeUserId is { } assignee)
                {
                    SetAssigned(chore, assignee, now);
                }
                else
                {
                    ClearToPile(chore);
                }

                context.Chores.Add(chore);
                AppendEvent(context, chore, ChoreEventType.Created, actorUserId, targetUserId: null, now);

                // Multi-person named roster seed (D8): one Assigned event per member (validated, deduped). The
                // events save in the SAME transaction as the chore, so the (HouseholdId, ChoreId) FK resolves.
                if (chore.RequiredCount > 1 && cmd.AssignedUserIds is { Count: > 0 } assignedUserIds)
                {
                    foreach (var memberId in assignedUserIds.Distinct())
                    {
                        await EnsureHouseholdMemberAsync(context, householdId, memberId, cancellationToken);
                        AppendParticipation(context, chore, ChoreParticipationType.Assigned, memberId, actorUserId, now);
                    }
                }

                await context.SaveChangesAsync(cancellationToken);

                logger.LogInformation("Created Chore {ChoreId} for household {HouseholdId}", chore.ChoreId, householdId);
                return chore;
            },
            logger,
            "Chore");
    }

    public async Task<Chore> UpdateChoreAsync(int householdId, int choreId, UpdateChoreCommand cmd, uint version, CancellationToken cancellationToken = default)
    {
        ValidateName(cmd.Name);
        ValidateRecurrence(cmd.RecurrenceMode, cmd.IntervalDays, cmd.DaysOfWeek, cmd.DayOfMonth);
        ValidateRequiredCount(cmd.RequiredCount);

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var chore = await LoadChoreAsync(context, householdId, choreId, cancellationToken);
        var previousRequiredCount = chore.RequiredCount;

        // Delete-on-replace for the photo (M8) — drop the old file when the path changes.
        if (!string.Equals(chore.PhotoPath, cmd.PhotoPath, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(chore.PhotoPath))
        {
            await imageService.DeleteImageAsync(chore.PhotoPath, cancellationToken);
        }

        chore.Name = cmd.Name.Trim();
        chore.Description = string.IsNullOrWhiteSpace(cmd.Description) ? null : cmd.Description.Trim();
        chore.Icon = cmd.Icon ?? string.Empty;
        chore.RoomId = cmd.RoomId;
        chore.RecurrenceMode = cmd.RecurrenceMode;
        chore.IntervalDays = cmd.IntervalDays;
        chore.AnchorDate = cmd.AnchorDate;
        chore.DaysOfWeek = cmd.DaysOfWeek;
        chore.DayOfMonth = cmd.DayOfMonth;
        chore.EffortTier = cmd.EffortTier;
        chore.EffortPoints = ChoreEffort.PointsFor(cmd.EffortTier);
        chore.RequiredCount = cmd.RequiredCount;
        chore.OwnerUserId = cmd.OwnerUserId;
        chore.PhotoPath = cmd.PhotoPath;
        // Assignment is otherwise NOT touched by an edit (it moves via claim/drop/hand-off/complete) — EXCEPT
        // when RequiredCount crosses the single↔multi boundary (D9), which migrates the representation.
        if (previousRequiredCount == 1 && cmd.RequiredCount > 1)
        {
            // 1 → N: preserve a lone trio assignee as an Assigned roster member, then free the trio to the pile.
            if (chore.AssigneeUserId is { } heldBy && chore.AssignmentKind != AssignmentKind.None)
            {
                AppendParticipation(context, chore, ChoreParticipationType.Assigned, heldBy, heldBy, UtcNow());
            }
            ClearToPile(chore);
        }
        else if (previousRequiredCount > 1 && cmd.RequiredCount == 1)
        {
            // N → 1: back to single-person; clear the (already-None) trio to the pile. Dormant participation
            // events are simply ignored by the X=1 read path (D3).
            ClearToPile(chore);
        }

        await SaveWithConcurrencyAsync(context, chore, version, cancellationToken);
        logger.LogInformation("Updated Chore {ChoreId} for household {HouseholdId}", choreId, householdId);
        return chore;
    }

    public async Task DeleteChoreAsync(int householdId, int choreId, uint version, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var chore = await LoadChoreAsync(context, householdId, choreId, cancellationToken);

        // Delete the chore's photo from disk before removing the row (M8 — EF cascade does not touch the
        // filesystem). No-op safe when PhotoPath is null/empty.
        if (!string.IsNullOrWhiteSpace(chore.PhotoPath))
        {
            await imageService.DeleteImageAsync(chore.PhotoPath, cancellationToken);
        }

        context.Chores.Remove(chore);
        await SaveWithConcurrencyAsync(context, chore, version, cancellationToken);
        logger.LogInformation("Deleted Chore {ChoreId} for household {HouseholdId}", choreId, householdId);
    }

    // -------------------------------------------------------- claim state machine

    public async Task<Chore> ClaimAsync(int householdId, int choreId, int actorUserId, uint version, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var chore = await LoadChoreAsync(context, householdId, choreId, cancellationToken);
        var now = UtcNow();

        // Lazy auto-release (D7): if the current hold is a stale self-claim, free it to the pile first so
        // this claim can proceed. A deliberately-Assigned chore is NEVER auto-released (the D6 guard).
        MaterializeAutoReleaseIfStale(context, chore, now);

        // Precondition: the chore must be on the pile to claim it (MN8 — reject claiming a held chore).
        if (chore.AssigneeUserId is not null)
        {
            throw new ChoreValidationException(
                $"Chore {choreId} is already held by user {chore.AssigneeUserId} ({chore.AssignmentKind}); cannot claim.");
        }

        SetClaimed(chore, actorUserId, now);
        AppendEvent(context, chore, ChoreEventType.Claimed, actorUserId, targetUserId: null, now);

        await SaveWithConcurrencyAsync(context, chore, version, cancellationToken);
        logger.LogInformation("Chore {ChoreId} claimed by user {UserId} (household {HouseholdId})", choreId, actorUserId, householdId);
        return chore;
    }

    public async Task<Chore> DropAsync(int householdId, int choreId, int actorUserId, uint version, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var chore = await LoadChoreAsync(context, householdId, choreId, cancellationToken);
        var now = UtcNow();

        MaterializeAutoReleaseIfStale(context, chore, now);

        // Drop is Claimed-only and holder-only (council M9, MN8): a deliberately-Assigned chore is freed via
        // hand-off, not drop; you can only drop a claim you actually hold.
        if (chore.AssignmentKind != AssignmentKind.Claimed || chore.AssigneeUserId != actorUserId)
        {
            throw new ChoreValidationException(
                $"User {actorUserId} cannot drop chore {choreId}: it is not a self-claim held by this user " +
                $"(kind={chore.AssignmentKind}, assignee={chore.AssigneeUserId?.ToString() ?? "none"}).");
        }

        ClearToPile(chore);
        AppendEvent(context, chore, ChoreEventType.Dropped, actorUserId, targetUserId: null, now);

        await SaveWithConcurrencyAsync(context, chore, version, cancellationToken);
        logger.LogInformation("Chore {ChoreId} dropped by user {UserId} (household {HouseholdId})", choreId, actorUserId, householdId);
        return chore;
    }

    public async Task<Chore> HandOffAsync(int householdId, int choreId, int actorUserId, int? targetUserId, uint version, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var chore = await LoadChoreAsync(context, householdId, choreId, cancellationToken);
        var now = UtcNow();

        MaterializeAutoReleaseIfStale(context, chore, now);

        if (targetUserId is { } target)
        {
            // Hand to a household member => deliberate Assigned (council M9; anyone can reassign, no roles).
            await EnsureHouseholdMemberAsync(context, householdId, target, cancellationToken);
            SetAssigned(chore, target, now);
        }
        else
        {
            // null target => return to the pile (the stuck-Assigned escape hatch, council M9).
            ClearToPile(chore);
        }

        AppendEvent(context, chore, ChoreEventType.HandedOff, actorUserId, targetUserId, now);

        await SaveWithConcurrencyAsync(context, chore, version, cancellationToken);
        logger.LogInformation("Chore {ChoreId} handed off by user {ActorId} to {Target} (household {HouseholdId})",
            choreId, actorUserId, targetUserId?.ToString() ?? "pile", householdId);
        return chore;
    }

    public async Task<Chore> CompleteAsync(int householdId, int choreId, int actorUserId, string? note, string? photoPath, IReadOnlyList<int>? participantUserIds, uint version, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var chore = await LoadChoreAsync(context, householdId, choreId, cancellationToken);
        var now = UtcNow();

        // Auto-release a stale claim first so the post-complete pile/keep decision uses the true state.
        // (A stale claim that the completer is now satisfying is released to the pile, then the recurring
        // chore stays on the pile after completion — consistent with D7.)
        MaterializeAutoReleaseIfStale(context, chore, now);

        // Resolve the contributor set for THIS call: the actor plus any named participants (D7 name-others).
        // Each named member is validated for household membership (reusing EnsureHouseholdMemberAsync, M1);
        // the set dedupes within the call.
        var candidates = new HashSet<int> { actorUserId };
        if (participantUserIds is not null)
        {
            foreach (var participantId in participantUserIds)
            {
                candidates.Add(participantId);
            }
        }

        foreach (var candidateId in candidates)
        {
            await EnsureHouseholdMemberAsync(context, householdId, candidateId, cancellationToken);
        }

        // Prior distinct contributors toward the CURRENT open occurrence (rows after LastCompletedAt, M7 —
        // filtered by HouseholdId AND ChoreId). The count is derived, never stored (M5/MN6).
        var priorCompletions = await context.ChoreCompletions
            .Where(cc => cc.HouseholdId == householdId && cc.ChoreId == choreId)
            .ToListAsync(cancellationToken);
        var priorSet = ChoreCompletionProgress.DistinctContributorsSince(priorCompletions, chore.LastCompletedAt);

        // Only members not already counted toward the current occurrence add anything (D6 distinctness).
        var newContributors = candidates.Where(id => !priorSet.Contains(id)).ToHashSet();

        // Nothing this call would add => reject (D6). Critically, an actor already in priorSet who names a
        // NEW participant has a non-empty newContributors set and does NOT throw (D7 escape hatch).
        if (newContributors.Count == 0)
        {
            throw new ChoreValidationException("You've already marked your part — waiting on the others.");
        }

        // Write one ChoreCompletion per newly-contributing member — full effort points each (D5). The
        // note/photo attach to the actor's OWN row iff the actor is newly contributing; otherwise discarded.
        foreach (var contributorId in newContributors)
        {
            var attachActorPayload = contributorId == actorUserId;
            context.ChoreCompletions.Add(new ChoreCompletion
            {
                HouseholdId = householdId,
                ChoreId = choreId,
                CompletedByUserId = contributorId,   // council M8 — the completer, even if a different user held it
                CompletedAt = now,
                EffortPointsSnapshot = chore.EffortPoints,
                Note = attachActorPayload && !string.IsNullOrWhiteSpace(note) ? note.Trim() : null,
                PhotoPath = attachActorPayload ? photoPath : null
            });
        }

        var newDistinctCount = priorSet.Count + newContributors.Count;

        // Every contribution (partial OR final) stamps LastContributionAt and FORCES the row UPDATE so the
        // xmin concurrency check always fires (D3/M4). EF emits no UPDATE for a row with no changed scalar,
        // and a same-tick / fixed-clock `now` could equal the existing value — so mark it modified
        // explicitly. Without this, two racing partials can both skip the UPDATE, never serialize, and wedge
        // the chore at "N of N, not advanced" (E2). LOAD-BEARING — do not remove.
        chore.LastContributionAt = now;
        context.Entry(chore).Property(c => c.LastContributionAt).IsModified = true;

        if (ChoreCompletionProgress.IsSatisfied(newDistinctCount, chore.RequiredCount))
        {
            // SATISFYING completion — run the existing advance logic UNCHANGED.
            chore.LastCompletedAt = now;

            if (chore.RecurrenceMode == RecurrenceMode.OneOff)
            {
                // OneOff: lifecycle terminates. The board (WP-05) excludes Done chores.
                chore.Status = ChoreStatus.Done;
            }
            // Flexible/Fixed: recurrence advance is DERIVED, not stored. Flexible decays off the new
            // LastCompletedAt; Fixed derives the next slot from the unchanged AnchorDate/cadence — we mutate
            // NO recurrence field here (do not rewrite AnchorDate). The calculator owns the next-due derivation.

            // Post-completion assignment: a recurring chore held by a self-claim returns to the pile so it is
            // up-for-grabs again; a deliberately-Assigned chore keeps its sticky assignee.
            if (chore.RecurrenceMode != RecurrenceMode.OneOff && chore.AssignmentKind == AssignmentKind.Claimed)
            {
                ClearToPile(chore);
            }
        }
        // Else (partial): leave LastCompletedAt, Status, and the assignment trio untouched (D4) — only the
        // ChoreCompletion rows + LastContributionAt above were written.

        await SaveWithConcurrencyAsync(context, chore, version, cancellationToken);
        logger.LogInformation(
            "Chore {ChoreId} contribution by user {UserId} (+{New} contributor(s), {Distinct} of {Required}) (household {HouseholdId})",
            choreId, actorUserId, newContributors.Count, newDistinctCount, chore.RequiredCount, householdId);
        return chore;
    }

    // -------------------------------------------------------- roster (multi-person named soft roster, rework)

    public async Task<Chore> AssignToRosterAsync(int householdId, int choreId, int actorUserId, int subjectUserId, uint version, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var chore = await LoadChoreAsync(context, householdId, choreId, cancellationToken);
        EnsureMultiPerson(chore);
        await EnsureHouseholdMemberAsync(context, householdId, subjectUserId, cancellationToken);

        var now = UtcNow();
        AppendParticipation(context, chore, ChoreParticipationType.Assigned, subjectUserId, actorUserId, now);
        MarkChoreTouched(context, chore, now);

        await SaveWithConcurrencyAsync(context, chore, version, cancellationToken);
        logger.LogInformation("Chore {ChoreId} roster: user {Subject} assigned by {Actor} (household {HouseholdId})",
            choreId, subjectUserId, actorUserId, householdId);
        return chore;
    }

    public async Task<Chore> CommitToRosterAsync(int householdId, int choreId, int actorUserId, uint version, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var chore = await LoadChoreAsync(context, householdId, choreId, cancellationToken);
        EnsureMultiPerson(chore);
        // The caller is a resolved household member; assert defensively (M1).
        await EnsureHouseholdMemberAsync(context, householdId, actorUserId, cancellationToken);

        var now = UtcNow();
        AppendParticipation(context, chore, ChoreParticipationType.Committed, actorUserId, actorUserId, now);
        MarkChoreTouched(context, chore, now);

        await SaveWithConcurrencyAsync(context, chore, version, cancellationToken);
        logger.LogInformation("Chore {ChoreId} roster: user {UserId} committed (household {HouseholdId})",
            choreId, actorUserId, householdId);
        return chore;
    }

    public async Task<Chore> LeaveRosterAsync(int householdId, int choreId, int actorUserId, int? subjectUserId, uint version, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var chore = await LoadChoreAsync(context, householdId, choreId, cancellationToken);
        EnsureMultiPerson(chore);

        var subject = subjectUserId ?? actorUserId;
        if (subject != actorUserId)
        {
            // Removing SOMEONE ELSE requires the actor be the chore creator or owner (non-punitive default:
            // anyone may remove themselves; only the owner/creator may remove others).
            if (actorUserId != chore.EnteredByUserId && actorUserId != chore.OwnerUserId)
            {
                throw new ChoreValidationException(
                    $"User {actorUserId} cannot remove user {subject} from chore {choreId}'s roster " +
                    "(only the creator or owner may remove others).");
            }
            await EnsureHouseholdMemberAsync(context, householdId, subject, cancellationToken);
        }

        var now = UtcNow();
        AppendParticipation(context, chore, ChoreParticipationType.Left, subject, actorUserId, now);
        MarkChoreTouched(context, chore, now);

        await SaveWithConcurrencyAsync(context, chore, version, cancellationToken);
        logger.LogInformation("Chore {ChoreId} roster: user {Subject} left (by {Actor}, household {HouseholdId})",
            choreId, subject, actorUserId, householdId);
        return chore;
    }

    // ------------------------------------------------------------ helpers

    /// <summary>
    /// Lazy auto-release (D7, MN1): if <paramref name="chore"/> is a STALE self-claim
    /// (<see cref="ChoreStatusCalculator.IsClaimStale"/>), clear the assignment trio to the pile and append a
    /// <c>ChoreEvent{AutoReleased}</c> whose <c>ActorUserId</c> is the LAPSED claimer (council M16). A
    /// deliberately-<see cref="AssignmentKind.Assigned"/> chore is never released — assignment is durable;
    /// only self-claims lapse. No-op when not stale.
    /// </summary>
    private void MaterializeAutoReleaseIfStale(ApplicationDbContext context, Chore chore, DateTime now)
    {
        if (!calculator.IsClaimStale(chore.AssignmentKind, chore.ClaimedAt, now))
        {
            return;
        }

        var lapsedClaimer = chore.AssigneeUserId;
        ClearToPile(chore);

        // The lapsed claimer is the actor on the AutoReleased event (council M16). AssigneeUserId is
        // guaranteed non-null here (IsClaimStale => Claimed => the invariant holds an assignee), but guard.
        AppendEvent(context, chore, ChoreEventType.AutoReleased, lapsedClaimer ?? chore.EnteredByUserId, targetUserId: null, now);

        logger.LogInformation("Chore {ChoreId} auto-released from stale claim by user {UserId} (household {HouseholdId})",
            chore.ChoreId, lapsedClaimer, chore.HouseholdId);
    }

    /// <summary>Atomically set the assignment trio to a deliberate Assigned state (council M1).</summary>
    private static void SetAssigned(Chore chore, int assigneeUserId, DateTime now)
    {
        chore.AssigneeUserId = assigneeUserId;
        chore.AssignmentKind = AssignmentKind.Assigned;
        chore.ClaimedAt = now;
    }

    /// <summary>Atomically set the assignment trio to a self-Claimed state (council M1).</summary>
    private static void SetClaimed(Chore chore, int claimerUserId, DateTime now)
    {
        chore.AssigneeUserId = claimerUserId;
        chore.AssignmentKind = AssignmentKind.Claimed;
        chore.ClaimedAt = now;
    }

    /// <summary>Atomically clear the assignment trio back to the pile (council M1 — all three together).</summary>
    private static void ClearToPile(Chore chore)
    {
        chore.AssigneeUserId = null;
        chore.AssignmentKind = AssignmentKind.None;
        chore.ClaimedAt = null;
    }

    private static void AppendEvent(ApplicationDbContext context, Chore chore, ChoreEventType type, int actorUserId, int? targetUserId, DateTime now)
    {
        context.ChoreEvents.Add(new ChoreEvent
        {
            HouseholdId = chore.HouseholdId,
            ChoreId = chore.ChoreId,
            Type = type,
            ActorUserId = actorUserId,
            TargetUserId = targetUserId,
            At = now
        });
    }

    /// <summary>Append a roster participation event (rework). <c>ParticipationEventId</c> is DB-generated.</summary>
    private static void AppendParticipation(ApplicationDbContext context, Chore chore, ChoreParticipationType type, int subjectUserId, int actorUserId, DateTime now)
    {
        context.ChoreParticipationEvents.Add(new ChoreParticipationEvent
        {
            HouseholdId = chore.HouseholdId,
            ChoreId = chore.ChoreId,
            Type = type,
            SubjectUserId = subjectUserId,
            ActorUserId = actorUserId,
            At = now
        });
    }

    /// <summary>
    /// Force the xmin UPDATE on every roster write (M3/D6) so concurrent roster mutations serialize on the
    /// single Chore-row frontier — the same discipline as <c>CompleteAsync</c>. EF emits no UPDATE for a row
    /// with no changed scalar (and a same-tick <c>now</c> could equal the stored value), so mark it modified.
    /// </summary>
    private static void MarkChoreTouched(ApplicationDbContext context, Chore chore, DateTime now)
    {
        chore.LastContributionAt = now;
        context.Entry(chore).Property(c => c.LastContributionAt).IsModified = true;
    }

    /// <summary>Reject a roster action on a single-person chore — the roster exists only for X &gt; 1 (D3).</summary>
    private static void EnsureMultiPerson(Chore chore)
    {
        if (chore.RequiredCount <= 1)
        {
            throw new ChoreValidationException(
                $"Chore {chore.ChoreId} is a single-person chore (RequiredCount={chore.RequiredCount}); " +
                "roster actions apply only to multi-person chores.");
        }
    }

    private static async Task<Chore> LoadChoreAsync(ApplicationDbContext context, int householdId, int choreId, CancellationToken cancellationToken)
    {
        return await context.Chores
            .FirstOrDefaultAsync(c => c.HouseholdId == householdId && c.ChoreId == choreId, cancellationToken)
            ?? throw new ChoreNotFoundException($"Chore {choreId} not found for household {householdId}.");
    }

    private static async Task EnsureHouseholdMemberAsync(ApplicationDbContext context, int householdId, int userId, CancellationToken cancellationToken)
    {
        var isMember = await context.Users
            .AnyAsync(u => u.Id == userId && u.HouseholdId == householdId, cancellationToken);

        if (!isMember)
        {
            throw new ChoreValidationException($"User {userId} is not a member of household {householdId}.");
        }
    }

    private static void ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ChoreValidationException("Chore name is required.");
        }
    }

    /// <summary>
    /// A Fixed/Flexible chore needs a cadence: a positive <c>IntervalDays</c> OR a non-empty
    /// <c>DaysOfWeek</c>. A <c>DayOfMonth</c>-only recurrence is rejected (D4-B — monthly-on-day deferred,
    /// E5 ceiling). OneOff has no cadence requirement.
    /// </summary>
    private static void ValidateRecurrence(RecurrenceMode mode, int? intervalDays, ChoreDaysOfWeek? daysOfWeek, int? dayOfMonth)
    {
        if (mode == RecurrenceMode.OneOff)
        {
            return;
        }

        var hasInterval = intervalDays is { } days && days > 0;
        var hasDaysOfWeek = daysOfWeek is { } dow && dow != ChoreDaysOfWeek.None;
        var hasDayOfMonth = dayOfMonth is { } dom && dom > 0;

        if (hasInterval || hasDaysOfWeek)
        {
            return;
        }

        if (hasDayOfMonth)
        {
            throw new ChoreValidationException(
                "Monthly-on-day recurrence (DayOfMonth) is not supported in v1 (D4-B). " +
                "Use a positive IntervalDays or a DaysOfWeek selection.");
        }

        throw new ChoreValidationException(
            $"A {mode} chore requires a positive IntervalDays or a DaysOfWeek selection.");
    }

    /// <summary>
    /// The multi-person requirement (D2): <c>1</c> = a normal single-completion chore (the default);
    /// <c>&gt;= 2</c> = co-sign. Reject <c>&lt; 1</c>. There is NO server-side upper bound (a member can leave;
    /// the create/edit UI caps the picker at the household member count, WP-06; an unsatisfiable count is
    /// recovered by editing it down, E1) — do not clamp.
    /// </summary>
    private static void ValidateRequiredCount(int requiredCount)
    {
        if (requiredCount < 1)
        {
            throw new ChoreValidationException("RequiredCount must be at least 1.");
        }
    }

    /// <summary>
    /// Saves, mapping an xmin optimistic-concurrency conflict to <see cref="ChoreConflictException"/> (→ 409).
    /// <para>Council C1 — the actual mechanism: loading the row makes EF treat the LOADED <c>Version</c> as
    /// the original, so a stale client token would NOT conflict. We set the client token as the
    /// <c>OriginalValue</c> before saving so a concurrent write surfaces as
    /// <see cref="DbUpdateConcurrencyException"/>, which we rethrow as a typed conflict. We do NOT swallow or
    /// retry (MN — last-write-wins is the bug WP-08 catches against real Postgres).</para>
    /// </summary>
    private async Task SaveWithConcurrencyAsync(ApplicationDbContext context, Chore chore, uint clientVersion, CancellationToken cancellationToken)
    {
        context.Entry(chore).Property(c => c.Version).OriginalValue = clientVersion;

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ChoreConflictException(
                $"Chore {chore.ChoreId} was modified by another user; the supplied version is stale.", ex);
        }
    }
}
