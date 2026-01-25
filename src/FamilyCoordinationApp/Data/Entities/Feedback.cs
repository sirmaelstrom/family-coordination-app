namespace FamilyCoordinationApp.Data.Entities;

public enum FeedbackType
{
    Bug,
    FeatureRequest,
    General
}

public class Feedback
{
    public int Id { get; set; }
    public int? UserId { get; set; }
    public int? HouseholdId { get; set; }
    public FeedbackType Type { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? CurrentPage { get; set; }
    public string? UserAgent { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsRead { get; set; }
    public bool IsResolved { get; set; }
    public string? AdminNotes { get; set; }

    // Navigation
    public User? User { get; set; }
    public Household? Household { get; set; }
}
