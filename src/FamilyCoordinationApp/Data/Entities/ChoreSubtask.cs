namespace FamilyCoordinationApp.Data.Entities;

/// <summary>
/// A lightweight checklist item on a Chore. Anyone in the household may add/rename/check/delete it; it
/// NEVER gates chore completion. Last-write-wins — this table carries NO concurrency token, and subtask
/// writes must never touch Chore.Version. On the SATISFYING completion of a recurring chore, all of its
/// subtasks reset to IsDone=false (handled in ChoreService.CompleteAsync).
/// </summary>
public class ChoreSubtask
{
    public int HouseholdId { get; set; }
    public int ChoreId { get; set; }
    public int SubtaskId { get; set; }   // DB-generated (identity)

    public string Title { get; set; } = string.Empty;
    public bool IsDone { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }  // UTC

    public Chore Chore { get; set; } = default!;
}
