namespace FamilyCoordinationApp.Data.Entities;

/// <summary>
/// A member's personal access token for an agent client (quest 8bfa42cf): the credential the <c>/mcp</c> endpoint
/// accepts as <c>Authorization: Bearer fca_pat_…</c>. Only the SHA-256 hash of the secret is stored, so a database
/// read cannot recover a usable token.
/// <para>Bound at mint to one member AND that member's household. Every request re-checks that the member still
/// exists, is enabled, and still belongs to <see cref="HouseholdId"/>; a member who moved household does not carry
/// old tokens into the new one. The expiry is absolute (never sliding), and a revoke is immediate.</para>
/// </summary>
/// <remarks>No row version: nothing loads and saves a token. Every change (revoke, last-use stamp) is one
/// <c>ExecuteUpdate</c> statement, so a revoke can never lose a race to a concurrent use.</remarks>
public class PersonalAccessToken : Tenancy.ITenantEntity, Tenancy.IAuditable
{
    public int Id { get; set; }
    public int HouseholdId { get; set; }

    /// <summary>The member the token acts as. Writes made with it are audited to this user.</summary>
    public int UserId { get; set; }

    /// <summary>The member's name for the client holding it, e.g. "Claude Code on the desktop".</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Uppercase hex SHA-256 of the full secret (<c>fca_pat_…</c>). Unique.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>The first characters of the secret, safe to show so the member can tell tokens apart.</summary>
    public string SafePrefix { get; set; } = string.Empty;

    public AgentTokenAccess Access { get; set; } = AgentTokenAccess.Read;

    public DateTime CreatedAt { get; set; }

    /// <summary>Absolute expiry (UTC). The token is refused at or after this instant.</summary>
    public DateTime ExpiresAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    /// <summary>Last successful use (UTC), refreshed at most once a minute.</summary>
    public DateTime? LastUsedAt { get; set; }

    public Household Household { get; set; } = default!;
    public User User { get; set; } = default!;
}

/// <summary>What a <see cref="PersonalAccessToken"/> may do. Stored as a string.</summary>
public enum AgentTokenAccess
{
    /// <summary>The read tools only.</summary>
    Read,

    /// <summary>The read tools plus the bounded write tools. Not admin: no deletes, members, settings or tokens.</summary>
    ReadWrite,
}
