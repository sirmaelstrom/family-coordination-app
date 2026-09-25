namespace FamilyCoordinationApp.Tenancy;

/// <summary>Which tenant, if any, the current DI scope is acting for (fca-household-scope D2).</summary>
public enum TenantState
{
    /// <summary>The default: nothing has set a tenant yet.</summary>
    Unset,

    /// <summary>A resolved HTTP caller, set once per request by <see cref="CallerTenantMiddleware"/>.</summary>
    Caller,

    /// <summary>System work for one household, entered by <see cref="ITenantContext.RunAs"/>.</summary>
    System,

    /// <summary>
    /// No tenant scoping at all. Reachable ONLY through <see cref="TenantContext.CreateUnfiltered"/>, which only
    /// <c>ApplicationDbContext</c>'s options-only constructor calls (D12; pinned by the architecture guard).
    /// </summary>
    Unfiltered,
}

/// <summary>What an Unset tenant means outside any HTTP request (D15).</summary>
public enum OutOfRequestMode
{
    /// <summary>Fail loudly. The default in code and what production gets.</summary>
    Throw,

    /// <summary>Behave unfiltered. Set only by the test hosts (<c>TestHostTenancy.Apply</c>).</summary>
    Unfiltered,
}

/// <summary>
/// The scoped tenant of the current DI scope: set per request by the caller-resolution middleware, entered for
/// system work by <see cref="RunAs"/>, and read by every context the scoped <see cref="TenantDbContextFactory"/>
/// creates.
/// <para><b>A cross-WP contract</b> (fca-household-scope WP-01): WP-02's filter, WP-03's write step and WP-04's
/// <see cref="CallerScope"/> binding read these members, and none of them may change them.</para>
/// </summary>
public interface ITenantContext
{
    /// <summary>The current state (<see cref="RunAs"/> → System; restored on dispose).</summary>
    TenantState State { get; }

    /// <summary>The Caller or System household; <c>null</c> otherwise.</summary>
    int? HouseholdId { get; }

    /// <summary>The Caller's user id; <c>null</c> otherwise (and <c>null</c> inside <see cref="RunAs"/>).</summary>
    int? UserId { get; }

    /// <summary><see cref="State"/> is <see cref="TenantState.Unfiltered"/> (a <see cref="TenantContext.CreateUnfiltered"/> instance).</summary>
    bool IsUnfiltered { get; }

    /// <summary><see cref="State"/> is Unset and there is no current HTTP request (D15).</summary>
    bool IsOutOfRequest { get; }

    /// <summary>From <c>Tenancy:OutOfRequest</c> (<see cref="TenancyOptions.OutOfRequest"/>).</summary>
    OutOfRequestMode OutOfRequestMode { get; }

    /// <summary><c>true</c> inside an <see cref="AllowCrossTenantWrite"/> scope.</summary>
    bool IsCrossTenantWriteAllowed { get; }

    /// <summary>Set the resolved HTTP caller. Middleware only; valid only from <see cref="TenantState.Unset"/>.</summary>
    void SetCaller(int householdId, int userId);

    /// <summary>
    /// Act as <see cref="TenantState.System"/> for <paramref name="householdId"/> until the returned scope is
    /// disposed, which restores the exact previous state. Nests. A no-op on an Unfiltered instance.
    /// </summary>
    IDisposable RunAs(int householdId);

    /// <summary>Permit a write to another household's row until the returned scope is disposed (D9). Nests.</summary>
    IDisposable AllowCrossTenantWrite(string reason);
}
