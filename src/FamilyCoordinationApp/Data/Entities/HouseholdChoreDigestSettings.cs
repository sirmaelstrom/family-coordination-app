namespace FamilyCoordinationApp.Data.Entities;

/// <summary>
/// Per-household settings for the weekly chore equity digest (WP-01).
/// PK is <see cref="HouseholdId"/> (1:1 with Household) — value-generated-never.
/// <see cref="WebhookUrlProtected"/> holds ciphertext; encryption/decryption lives in WP-03.
/// </summary>
public class HouseholdChoreDigestSettings
{
    /// <summary>PK and FK to <see cref="Household"/>. Caller-supplied; never generated.</summary>
    public int HouseholdId { get; set; }

    /// <summary>Encrypted webhook URL (ciphertext). Null when not yet configured.</summary>
    public string? WebhookUrlProtected { get; set; }

    /// <summary>Whether digest sending is enabled for this household. Defaults to false.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>How often the digest fires. Stored as string. Defaults to Weekly.</summary>
    public DigestCadence Cadence { get; set; } = DigestCadence.Weekly;

    /// <summary>Day of week the digest is sent. Stored as int by EF default.</summary>
    public DayOfWeek SendDayOfWeek { get; set; } = DayOfWeek.Sunday;

    /// <summary>Hour (0–23, local household time) at which the digest is sent. Defaults to 18.</summary>
    public int SendHourLocal { get; set; } = 18;

    /// <summary>UTC timestamp of the last successful digest send. Null if never sent.</summary>
    public DateTime? LastSentAt { get; set; }

    /// <summary>UTC timestamp of row creation.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC timestamp of last update.</summary>
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public Household Household { get; set; } = default!;
}
