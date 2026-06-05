using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services.Interfaces;

/// <summary>
/// Writes + the claim state machine for chores (WP-04): CRUD, claim/drop/hand-off/complete (D6/D7/D8), the
/// completion log + recurrence clock advance, lazy auto-release of stale claims, and <c>ChoreEvent</c>
/// logging. Reads/board projection live in <c>IChoreBoardService</c> (WP-05).
/// <para><b>Return shape:</b> mutating methods return the updated <see cref="Chore"/> entity (with its current
/// assignment trio + advanced <c>Version</c> token). The endpoint layer (WP-06) maps it to the board
/// <c>ChoreDto</c> via the WP-05 projection — keeping the DTO definition entirely in WP-05's file
/// (the two are disjoint same-wave packages).</para>
/// <para><b>Concurrency:</b> mutating methods take the client's <paramref name="version"/> (xmin) token and
/// surface a stale token as <see cref="ChoreConflictException"/> (→ 409). Illegal transitions /
/// validation failures throw <see cref="ChoreValidationException"/> (→ 400); a missing chore throws
/// <see cref="ChoreNotFoundException"/> (→ 404).</para>
/// <para><b>HouseholdId</b> is always supplied by the caller from <c>ResolveUserAsync</c> (M1) — never
/// client-supplied. <c>now</c> is injected via the calculator-provided clock for deterministic tests.</para>
/// </summary>
public interface IChoreService
{
    /// <summary>
    /// Creates a chore for the household, authored by <paramref name="actorUserId"/>. Validates the name and
    /// recurrence (a <c>DayOfMonth</c>-only recurrence is rejected, D4-B). A non-null
    /// <c>cmd.AssigneeUserId</c> seeds it as deliberately Assigned (target must be a household member);
    /// otherwise it lands on the pile. Appends a <c>ChoreEvent{Created}</c>.
    /// </summary>
    /// <exception cref="ChoreValidationException">Invalid name / recurrence, or an out-of-household assignee.</exception>
    Task<Chore> CreateChoreAsync(int householdId, int actorUserId, CreateChoreCommand cmd, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a chore's editable fields (does NOT touch the assignment trio). Optimistic-concurrency checked
    /// against <paramref name="version"/>.
    /// </summary>
    /// <exception cref="ChoreNotFoundException">No such chore in the household.</exception>
    /// <exception cref="ChoreValidationException">Invalid name / recurrence.</exception>
    /// <exception cref="ChoreConflictException">The client <paramref name="version"/> is stale.</exception>
    Task<Chore> UpdateChoreAsync(int householdId, int choreId, UpdateChoreCommand cmd, uint version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a chore (and its photo from disk, M8). Cascade removes its completions/events. Optimistic-
    /// concurrency checked against <paramref name="version"/>.
    /// </summary>
    /// <exception cref="ChoreNotFoundException">No such chore in the household.</exception>
    /// <exception cref="ChoreConflictException">The client <paramref name="version"/> is stale.</exception>
    Task DeleteChoreAsync(int householdId, int choreId, uint version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims a chore for <paramref name="actorUserId"/>. Precondition: the chore is on the pile
    /// (<c>AssigneeUserId == null</c>) — a stale claim by another user is auto-released first (lazy). Sets the
    /// assignment trio atomically to <c>(actor, Claimed, now)</c> and appends a <c>ChoreEvent{Claimed}</c>.
    /// </summary>
    /// <exception cref="ChoreNotFoundException">No such chore in the household.</exception>
    /// <exception cref="ChoreValidationException">The chore is already held (illegal transition, MN8).</exception>
    /// <exception cref="ChoreConflictException">The client <paramref name="version"/> is stale.</exception>
    Task<Chore> ClaimAsync(int householdId, int choreId, int actorUserId, uint version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops a self-claim back to the pile. Precondition: <paramref name="actorUserId"/> holds it AND the
    /// hold is <see cref="AssignmentKind.Claimed"/> (drop is Claimed-only; a deliberately-Assigned chore is
    /// freed via hand-off, council M9). Clears the trio to <c>(null, None, null)</c> and appends a
    /// <c>ChoreEvent{Dropped}</c>.
    /// </summary>
    /// <exception cref="ChoreNotFoundException">No such chore in the household.</exception>
    /// <exception cref="ChoreValidationException">Actor does not hold it, or the hold is Assigned (illegal transition, MN8).</exception>
    /// <exception cref="ChoreConflictException">The client <paramref name="version"/> is stale.</exception>
    Task<Chore> DropAsync(int householdId, int choreId, int actorUserId, uint version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hands a chore off. <paramref name="targetUserId"/> = a household member → assigns it to them
    /// (<c>(target, Assigned, now)</c>); <c>null</c> → returns it to the pile (<c>(null, None, null)</c>) — the
    /// escape hatch for a stuck Assigned chore (council M9, collaborative/non-punitive, no roles). Appends a
    /// <c>ChoreEvent{HandedOff}</c> (TargetUserId = the new assignee, or null for return-to-pile).
    /// </summary>
    /// <exception cref="ChoreNotFoundException">No such chore in the household.</exception>
    /// <exception cref="ChoreValidationException">The target is outside the household (illegal transition, MN8).</exception>
    /// <exception cref="ChoreConflictException">The client <paramref name="version"/> is stale.</exception>
    Task<Chore> HandOffAsync(int householdId, int choreId, int actorUserId, int? targetUserId, uint version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a completion contribution toward a chore (council M8 — any household member may complete; an
    /// unclaimed pile chore may be completed directly; <c>CompletedByUserId = actorUserId</c> even if a
    /// different user held the claim). Multi-person (co-sign, D1=C): the chore advances only when
    /// <b>distinct</b> contributors toward the current occurrence reach <c>RequiredCount</c>.
    /// <para>The contributor set for this call is <c>{actorUserId} ∪ participantUserIds</c> (the D7 name-others
    /// escape hatch — each named member validated via household membership). One <see cref="ChoreCompletion"/>
    /// is written per <b>newly-contributing</b> member (full <c>EffortPoints</c> each, D5); members who already
    /// contributed to the current occurrence are silently skipped. If nothing new would be added (the actor
    /// already contributed and named no new participant), a <see cref="ChoreValidationException"/> is thrown
    /// (D6). <c>note</c>/<c>photoPath</c> attach to the actor's own row iff the actor is newly contributing.</para>
    /// <para>Every contribution stamps <c>LastContributionAt = now</c> (forcing the xmin UPDATE so concurrent
    /// contributions serialize, D3). A <b>partial</b> contribution leaves <c>LastCompletedAt</c>, <c>Status</c>,
    /// and the assignment trio untouched (D4). The <b>satisfying</b> contribution runs the existing advance:
    /// sets <c>LastCompletedAt = now</c>; OneOff → <c>Status = Done</c>; a recurring chore held by Claimed
    /// returns to the pile; an Assigned chore keeps its sticky assignee.</para>
    /// </summary>
    /// <exception cref="ChoreNotFoundException">No such chore in the household.</exception>
    /// <exception cref="ChoreValidationException">A named participant is outside the household, or nothing new would be recorded (the actor already contributed, D6).</exception>
    /// <exception cref="ChoreConflictException">The client <paramref name="version"/> is stale.</exception>
    Task<Chore> CompleteAsync(int householdId, int choreId, int actorUserId, string? note, string? photoPath, IReadOnlyList<int>? participantUserIds, uint version, CancellationToken cancellationToken = default);
}
