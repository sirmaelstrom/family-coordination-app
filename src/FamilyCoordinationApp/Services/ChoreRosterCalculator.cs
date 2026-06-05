using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// Pure derive-on-read fold for a multi-person (<c>RequiredCount &gt; 1</c>) chore's named soft roster
/// (rework). The current roster + each member's state are NEVER stored — they are folded on read from the
/// append-only <see cref="ChoreParticipationEvent"/> log plus the <see cref="ChoreCompletion"/> ledger,
/// exactly as <see cref="ChoreStatusCalculator"/> derives dueness and <see cref="ChoreCompletionProgress"/>
/// derives the contributor count. DB-free and side-effect-free (unit-testable without EF).
///
/// <para>The satisfaction gate is unchanged and lives elsewhere (<see cref="ChoreCompletionProgress"/>):
/// this calculator is the VISIBILITY + recurrence-carry-over layer, not the gate.</para>
///
/// <para>Fold rule (decisions D5/D7):
/// <list type="number">
///   <item><b>Window</b> participation events to the current occurrence (<c>At &gt; lastCompletedAt</c>, or
///   all when null), ordered by <c>At</c> then <c>ParticipationEventId</c>.</item>
///   <item><b>Base roster</b> (last-N-done carry-over): for a recurring chore with a prior completion, seed
///   the most-recent <c>requiredCount</c> distinct completers (<c>CompletedAt &lt;= lastCompletedAt</c>) as
///   soft <see cref="RosterState.Assigned"/> defaults. (First occurrence / one-off: no base — the create-time
///   Assigned events are themselves in-window.)</item>
///   <item><b>Apply window events</b>: <c>Left</c> removes the member; <c>Assigned</c> sets Assigned only if
///   absent (never downgrades an In member — monotonic); <c>Committed</c> sets In.</item>
///   <item><b>Overlay Done</b>: every current-occurrence completer becomes <see cref="RosterState.Done"/>
///   (added if absent — anyone may complete toward X; Done wins over a same-occurrence Left).</item>
/// </list></para>
/// </summary>
public static class ChoreRosterCalculator
{
    /// <summary>
    /// Fold the participation events + completion ledger into the current-occurrence roster.
    /// </summary>
    /// <param name="events">All participation events for the chore (windowed internally).</param>
    /// <param name="completions">All completion rows for the chore (used for the done-overlay AND the
    /// last-N-done base).</param>
    /// <param name="lastCompletedAt">The chore's <c>LastCompletedAt</c> (the current-occurrence boundary).</param>
    /// <param name="requiredCount">The chore's <c>RequiredCount</c> (X).</param>
    /// <param name="recurring">True for Fixed/Flexible chores (enables last-N-done carry-over).</param>
    public static DerivedRoster Fold(
        IEnumerable<ChoreParticipationEvent> events,
        IEnumerable<ChoreCompletion> completions,
        DateTime? lastCompletedAt,
        int requiredCount,
        bool recurring)
    {
        var completionList = completions as IReadOnlyList<ChoreCompletion> ?? completions.ToList();

        // Distinct completers toward the CURRENT occurrence — the satisfaction-relevant "done" set (M1: the
        // same windowing the gate uses).
        var doneUserIds = ChoreCompletionProgress.DistinctContributorsSince(completionList, lastCompletedAt);

        var state = new Dictionary<int, RosterState>();

        // 1. Last-N-done base (recurrence carry-over, D5) — soft Assigned defaults.
        if (recurring && lastCompletedAt is not null)
        {
            foreach (var userId in LastNDoneDefaults(completionList, lastCompletedAt.Value, requiredCount))
            {
                state[userId] = RosterState.Assigned;
            }
        }

        // 2. Apply current-occurrence events in chronological order (monotonic — D7/MN9).
        var windowEvents = events
            .Where(e => lastCompletedAt is null || e.At > lastCompletedAt.Value)
            .OrderBy(e => e.At)
            .ThenBy(e => e.ParticipationEventId);

        foreach (var e in windowEvents)
        {
            switch (e.Type)
            {
                case ChoreParticipationType.Left:
                    state.Remove(e.SubjectUserId);
                    break;
                case ChoreParticipationType.Assigned:
                    // Set Assigned only if absent — never downgrade an In/committed member.
                    if (!state.ContainsKey(e.SubjectUserId))
                    {
                        state[e.SubjectUserId] = RosterState.Assigned;
                    }
                    break;
                case ChoreParticipationType.Committed:
                    state[e.SubjectUserId] = RosterState.In;
                    break;
            }
        }

        // 3. Overlay Done — completion wins over any participation state (and adds non-rostered completers).
        foreach (var userId in doneUserIds)
        {
            state[userId] = RosterState.Done;
        }

        var members = state
            .Select(kv => new RosterMemberDto(kv.Key, kv.Value))
            .OrderBy(m => m.UserId)
            .ToList();

        return new DerivedRoster(members, doneUserIds.Count);
    }

    /// <summary>
    /// "Last N done": the most-recent <paramref name="requiredCount"/> distinct <c>CompletedByUserId</c>
    /// among completions with <c>CompletedAt &lt;= lastCompletedAt</c> (the previous occurrence's doers),
    /// most-recent first. Stable tie-break by <c>CompletionId</c> descending.
    /// </summary>
    public static IReadOnlyList<int> LastNDoneDefaults(
        IReadOnlyList<ChoreCompletion> completions, DateTime lastCompletedAt, int requiredCount)
    {
        var target = Math.Max(1, requiredCount);
        var result = new List<int>();
        var seen = new HashSet<int>();

        foreach (var c in completions
            .Where(c => c.CompletedAt <= lastCompletedAt)
            .OrderByDescending(c => c.CompletedAt)
            .ThenByDescending(c => c.CompletionId))
        {
            if (seen.Add(c.CompletedByUserId))
            {
                result.Add(c.CompletedByUserId);
                if (result.Count >= target)
                {
                    break;
                }
            }
        }

        return result;
    }
}

/// <summary>
/// The derived roster for one chore's current occurrence: the ordered member list (by userId) and the
/// distinct-done count (0..RequiredCount). Returned by <see cref="ChoreRosterCalculator.Fold"/>.
/// </summary>
public sealed record DerivedRoster(IReadOnlyList<RosterMemberDto> Members, int CompletedCount);
