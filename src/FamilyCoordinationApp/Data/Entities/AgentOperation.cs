namespace FamilyCoordinationApp.Data.Entities;

/// <summary>
/// The record of one agent write (quest 8bfa42cf), keyed by the caller-chosen request id, so a retried request is
/// answered from here instead of running twice. Written in the SAME transaction as the write's domain rows: a
/// record exists exactly when its write committed.
/// <para>Same member + same request id + same payload replays <see cref="ResultJson"/>; a different payload under
/// that id is a conflict. A record older than the retention window is refused rather than run again.</para>
/// </summary>
public class AgentOperation : Tenancy.ITenantEntity, Tenancy.IAuditable
{
    public int Id { get; set; }
    public int HouseholdId { get; set; }
    public int UserId { get; set; }

    /// <summary>The caller-chosen idempotency key, unique per member.</summary>
    public string RequestId { get; set; } = string.Empty;

    /// <summary>The tool that ran, e.g. <c>add_meal</c>.</summary>
    public string Tool { get; set; } = string.Empty;

    /// <summary>Uppercase hex SHA-256 of the tool name and its normalized arguments.</summary>
    public string PayloadHash { get; set; } = string.Empty;

    /// <summary>The committed result, as returned the first time.</summary>
    public string ResultJson { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public Household Household { get; set; } = default!;
    public User User { get; set; } = default!;
}
