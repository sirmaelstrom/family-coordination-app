using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Services.Interfaces;

/// <summary>
/// CRUD for a chore's lightweight checklist (Phase 14). Distinct from <see cref="IChoreService"/> because
/// subtask writes are <b>versionless / last-write-wins</b>: they carry NO xmin token and MUST never touch
/// <c>Chore.Version</c>. Every method is household-scoped (M1, never client-supplied); cross-household access
/// naturally 404s because the <c>(householdId, choreId, ...)</c> filter finds nothing. Subtasks NEVER gate
/// chore completion; on the satisfying completion of a recurring chore they reset to <c>IsDone=false</c>
/// (handled in <c>ChoreService.CompleteAsync</c>, not here).
/// </summary>
public interface IChoreSubtaskService
{
    /// <summary>
    /// Adds a checklist item to a chore. The title is trimmed and validated (required, ≤200 chars); a chore is
    /// capped at 50 items. <c>SortOrder</c> is appended (max existing + 1, or 0 if none). <c>SubtaskId</c> is
    /// DB-assigned.
    /// </summary>
    /// <exception cref="ChoreNotFoundException">No such chore in the household.</exception>
    /// <exception cref="ChoreValidationException">Blank/too-long title, or the 50-item cap is reached.</exception>
    Task<ChoreSubtaskDto> CreateAsync(int householdId, int choreId, string title, CancellationToken ct = default);

    /// <summary>
    /// Updates a checklist item — only the non-null fields are applied (title trimmed + validated if supplied).
    /// No version check; touches ONLY the subtask row (never the chore). When <paramref name="isDone"/> flips
    /// the item to done, <paramref name="actingUserId"/> is captured as the "who ticked it" actor (with a UTC
    /// timestamp); un-ticking clears the actor. Per-occurrence invariant: actor set IFF IsDone == true.
    /// </summary>
    /// <exception cref="ChoreNotFoundException">No such subtask in the household/chore.</exception>
    /// <exception cref="ChoreValidationException">A supplied title is blank or too long.</exception>
    Task<ChoreSubtaskDto> UpdateAsync(int householdId, int choreId, int subtaskId, int actingUserId, string? title, bool? isDone, int? sortOrder, CancellationToken ct = default);

    /// <summary>
    /// Re-orders a chore's checklist to a CONTIGUOUS 0..N-1 <c>SortOrder</c> in ONE write. The result is always
    /// a full permutation of the chore's subtasks: the provided ids that belong to the chore are placed first
    /// (de-duplicated, in the given order), then any OMITTED subtasks are appended in their current stable
    /// order, and all rows are renumbered — so a partial / duplicate / foreign-id list can never leave
    /// duplicate or gapped SortOrder. Household+chore scoped (M1); foreign ids are ignored. Versionless —
    /// never touches <c>Chore.Version</c>.
    /// </summary>
    Task ReorderAsync(int householdId, int choreId, IReadOnlyList<int> orderedSubtaskIds, CancellationToken ct = default);

    /// <summary>Deletes a checklist item.</summary>
    /// <exception cref="ChoreNotFoundException">No such subtask in the household/chore.</exception>
    Task DeleteAsync(int householdId, int choreId, int subtaskId, CancellationToken ct = default);
}
