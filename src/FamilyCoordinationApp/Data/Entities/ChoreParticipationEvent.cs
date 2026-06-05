namespace FamilyCoordinationApp.Data.Entities;

/// <summary>
/// Append-only participation event for a multi-person (<c>RequiredCount &gt; 1</c>) chore's named soft
/// roster. The current roster and each member's display state (assigned/in/done) are DERIVED on read by
/// folding these events — windowed to the current occurrence — plus the <see cref="ChoreCompletion"/>
/// ledger (see <c>ChoreRosterCalculator</c>). "Done" is NOT an event: it stays in
/// <see cref="ChoreCompletion"/> (the equity substrate) and is overlaid in the fold.
/// <para>Mirrors <see cref="ChoreEvent"/>: composite PK with a DB-generated
/// <see cref="ParticipationEventId"/>, Chore FK cascades, the two user FKs restrict (history survives a
/// user delete). The roster is single-frontier-serialized on the <c>Chore</c> row's xmin — this table
/// carries no concurrency token of its own (appends never conflict; the fold normalizes order).</para>
/// </summary>
public class ChoreParticipationEvent
{
    public int HouseholdId { get; set; }
    public int ChoreId { get; set; }
    public int ParticipationEventId { get; set; }

    public ChoreParticipationType Type { get; set; }

    /// <summary>The member this event is about (the person assigned, committing, or leaving).</summary>
    public int SubjectUserId { get; set; }

    /// <summary>Who performed the action — may differ from the subject (e.g. a creator assigning someone).</summary>
    public int ActorUserId { get; set; }

    public DateTime At { get; set; }  // UTC

    // Navigation
    public Chore Chore { get; set; } = default!;
    public User Subject { get; set; } = default!;
    public User Actor { get; set; } = default!;
}
