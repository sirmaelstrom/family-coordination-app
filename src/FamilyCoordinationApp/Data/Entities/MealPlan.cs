namespace FamilyCoordinationApp.Data.Entities;

public class MealPlan
{
    public int HouseholdId { get; set; }
    public int MealPlanId { get; set; }
    public DateOnly WeekStartDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    // Navigation
    public Household Household { get; set; } = default!;
    public ICollection<MealPlanEntry> Entries { get; set; } = new List<MealPlanEntry>();
}
