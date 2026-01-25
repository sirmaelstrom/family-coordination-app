namespace FamilyCoordinationApp.Data.Entities;

public enum HouseholdRequestStatus
{
    Pending,
    Approved,
    Rejected
}

public class HouseholdRequest
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;  // From Google auth
    public string DisplayName { get; set; } = string.Empty;  // From Google
    public string? GoogleId { get; set; }  // From Google auth
    public string HouseholdName { get; set; } = string.Empty;  // User-provided
    public HouseholdRequestStatus Status { get; set; } = HouseholdRequestStatus.Pending;
    public DateTime RequestedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewedBy { get; set; }  // Admin email who reviewed
    public string? RejectionReason { get; set; }
}
