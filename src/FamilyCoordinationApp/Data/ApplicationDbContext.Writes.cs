using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Tenancy;

namespace FamilyCoordinationApp.Data;

/// <summary>
/// The central write step (fca-household-scope D9/D16, WP-03). Before every save it walks the change tracker once:
/// <list type="bullet">
/// <item>An Added <see cref="IAuditable"/> with a <c>default</c> <c>CreatedAt</c> gets <see cref="Clock"/>'s now. This
/// runs in every tenant state and with the kill switch off: audit is independent of tenancy. Modified entries are
/// never touched.</item>
/// <item>Every Added, Modified or Deleted <see cref="ITenantEntity"/> is checked against the tenant: an Added row whose
/// <c>HouseholdId</c> is unset (0, or EF's temporary key) is stamped, parents before children, except under a
/// <c>Household</c> created in the same save, which is refused (<see cref="StampHouseholdId"/>); a row of another
/// household throws <see cref="CrossTenantWriteException"/> unless inside <c>AllowCrossTenantWrite</c>; a changed
/// <c>HouseholdId</c> always throws; an Unset tenant throws <see cref="TenantNotSetException"/>.</item>
/// <item>A Modified or Deleted row whose primary key excludes <c>HouseholdId</c> (<c>User</c>,
/// <c>HouseholdInvite</c>, <c>HouseholdCalendarToken</c>) must exist in the tenant's household, because its
/// <c>UPDATE … WHERE Id = @id</c> would otherwise reach a row whose tracked <c>HouseholdId</c> was forged. A row
/// that exists in ANOTHER household is refused; a row that exists nowhere (a concurrent delete) is left to base
/// <c>SaveChanges</c>, which raises <c>DbUpdateConcurrencyException</c> as before (<see cref="Owned{TEntity}"/>).</item>
/// </list>
/// The tenant checks are skipped with <c>Tenancy:EnforceWrites=false</c> (the D14 kill switch) and in the bypass
/// states (the options-only Unfiltered constructor, or out of request with <c>OutOfRequest=Unfiltered</c>), which
/// production cannot reach.
/// <para><b>What this step does NOT cover</b> (so don't rely on it for these):</para>
/// <list type="bullet">
/// <item>Non-tenant root writes: <c>Household</c>, <c>HouseholdConnection</c> and <c>Feedback</c> are not
/// <see cref="ITenantEntity"/>, so their writes pass unchecked, and so do the database cascades a <c>Household</c>
/// delete triggers.</item>
/// <item><c>ExecuteUpdate</c>/<c>ExecuteDelete</c> never pass through here. The read filter scopes the rows they
/// select, and <c>TenantWriteArchitectureTests</c> bans only a <c>SetProperty</c> that assigns <c>HouseholdId</c>.</item>
/// <item>Raw SQL through <c>Database</c>'s raw-SQL APIs (the fca#111 guard demands a <c>TENANT-SCOPE-OK</c> pragma on each call site).</item>
/// <item>An Added surrogate-key row with an explicit <c>Id</c> that collides with another household's row: the
/// ownership check runs for Modified/Deleted only, so this reaches the database as a primary-key violation
/// (<c>DbUpdateException</c>), not a <see cref="CrossTenantWriteException"/>.</item>
/// <item>References from a tenant row to another household's rows through a non-<c>HouseholdId</c> column (a
/// <c>Chore.OwnerUserId</c> naming another household's user): only <c>HouseholdId</c> is checked.</item>
/// </list>
/// </summary>
public partial class ApplicationDbContext
{
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        foreach (var check in PrepareWrites())
        {
            if (check.Ownership.Sync(this, check.Id, check.TenantHouseholdId)) continue;
            // Not in the tenant's household. A row that is gone (a concurrent delete) falls through, and base raises
            // DbUpdateConcurrencyException as it did before the write step. A row that exists elsewhere is refused.
            if (check.Ownership.ExistsSync(this, check.Id)) throw check.NotOwned();
        }
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        foreach (var check in PrepareWrites())
        {
            if (await check.Ownership.Async(this, check.Id, check.TenantHouseholdId, cancellationToken)) continue;
            // As in SaveChanges: absent falls through to base's concurrency exception; present elsewhere is refused.
            if (await check.Ownership.ExistsAsync(this, check.Id, cancellationToken)) throw check.NotOwned();
        }
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// The surrogate-key tenant types and their ownership queries. A unit test holds these keys equal to the
    /// model-derived set (tenant types whose primary key excludes <c>HouseholdId</c>), and a save of a surrogate-key
    /// tenant type missing here throws, so a new one can't slip past the check.
    /// </summary>
    internal static readonly IReadOnlyDictionary<Type, OwnershipQuery> OwnershipQueries = new Dictionary<Type, OwnershipQuery>
    {
        [typeof(User)] = OwnershipQuery.For<User>(),
        [typeof(HouseholdInvite)] = OwnershipQuery.For<HouseholdInvite>(),
        [typeof(HouseholdCalendarToken)] = OwnershipQuery.For<HouseholdCalendarToken>(),
    };

    /// <summary>
    /// Stamps and validates every pending write, and returns the surrogate-key entries whose ownership the caller
    /// must still query (sync or async, to match the save).
    /// </summary>
    private List<PendingOwnershipCheck> PrepareWrites()
    {
        var entries = ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();

        StampCreatedAt(entries);

        if (!Tenancy.EnforceWrites || BypassesTenantWrites) return [];

        var tenantEntries = entries.Where(e => e.Entity is ITenantEntity).ToList();
        if (tenantEntries.Count == 0) return [];

        if (Tenant.State is not (TenantState.Caller or TenantState.System) || Tenant.HouseholdId is not { } tenantHh)
        {
            throw WriteWithNoTenant();
        }

        StampHouseholdId(tenantEntries, tenantHh);
        return Validate(tenantEntries, tenantHh, Tenant.IsCrossTenantWriteAllowed);
    }

    /// <summary>The same bypass states as the read filter's, minus the kill switch, which has its own setting here.</summary>
    private bool BypassesTenantWrites =>
        Tenant.IsUnfiltered || (Tenant.IsOutOfRequest && Tenant.OutOfRequestMode == OutOfRequestMode.Unfiltered);

    /// <summary>D16: Added only, and only where unset, so every hand-set value keeps its exact behavior.</summary>
    private void StampCreatedAt(List<EntityEntry> entries)
    {
        DateTime? now = null;
        foreach (var entry in entries)
        {
            if (entry.State != EntityState.Added || entry.Entity is not IAuditable) continue;

            var createdAt = entry.Property(nameof(IAuditable.CreatedAt));
            if ((DateTime)createdAt.CurrentValue! == default)
            {
                now ??= Clock.GetUtcNow().UtcDateTime;
                createdAt.CurrentValue = now.Value;
            }
        }
    }

    /// <summary>
    /// Added rows whose code left <c>HouseholdId</c> unset take the tenant's household, principals first. Writing
    /// through the entry clears EF's temporary flag and lets it fix up the keys.
    /// <para><b>"Unset" is not just 0 here.</b> <c>HouseholdId</c> is a foreign key to the store-generated
    /// <c>Household.Id</c>, so when a row is added with 0, EF replaces it with a <b>temporary</b> value (and would
    /// refuse the save as an unknown key). So a temporary value counts as unset, unless it came from a
    /// <c>Household</c> principal tracked in this save (a household being created now): that row is left alone, and
    /// <see cref="Validate"/> refuses the save (<c>UnderANewHousehold</c>), inside <c>AllowCrossTenantWrite</c> too.</para>
    /// <para><b>The refusal is of the save, not of every stamp.</b> Only the non-tenant foreign key is checked here, so a
    /// tenant child of such a row (a <c>RecipeIngredient</c> under a new household's <c>Recipe</c>) got its temporary
    /// key through a tenant principal and IS stamped to the tenant in the tracker before the parent's refusal aborts
    /// the save. Nothing reaches the database, but the context stays mutated after the throw: discard it.</para>
    /// </summary>
    private static void StampHouseholdId(List<EntityEntry> tenantEntries, int tenantHh)
    {
        foreach (var entry in tenantEntries
                     .Where(e => e.State == EntityState.Added)
                     .OrderBy(e => TenantParentDepth(e.Metadata, [])))
        {
            var householdId = entry.Property(nameof(ITenantEntity.HouseholdId));
            var unset = (int)householdId.CurrentValue! == 0
                        || (householdId.IsTemporary && !HouseholdIdComesFromATrackedHousehold(entry));
            if (unset)
            {
                householdId.CurrentValue = tenantHh;
            }
        }
    }

    /// <summary>
    /// Is this row's <c>HouseholdId</c> the key of a non-tenant principal (a <c>Household</c>) that this context
    /// tracks? EF fixes up both navigation directions, so the dependent's reference is set either way.
    /// </summary>
    private static bool HouseholdIdComesFromATrackedHousehold(EntityEntry entry) =>
        entry.Metadata.GetForeignKeys()
            .Where(fk => fk.Properties.Any(p => p.Name == nameof(ITenantEntity.HouseholdId))
                         && !typeof(ITenantEntity).IsAssignableFrom(fk.PrincipalEntityType.ClrType))
            .Any(fk => fk.DependentToPrincipal is { } navigation && entry.Reference(navigation.Name).CurrentValue is not null);

    /// <summary>The longest chain of foreign keys from this type to other tenant types: 0 for a root.</summary>
    private static int TenantParentDepth(IReadOnlyEntityType type, HashSet<IReadOnlyEntityType> path)
    {
        if (!path.Add(type)) return 0;
        var depth = 0;
        foreach (var fk in type.GetForeignKeys())
        {
            var principal = fk.PrincipalEntityType;
            if (principal != type && typeof(ITenantEntity).IsAssignableFrom(principal.ClrType))
            {
                depth = Math.Max(depth, 1 + TenantParentDepth(principal, path));
            }
        }
        path.Remove(type);
        return depth;
    }

    private static List<PendingOwnershipCheck> Validate(List<EntityEntry> tenantEntries, int tenantHh, bool crossTenantAllowed)
    {
        var ownershipChecks = new List<PendingOwnershipCheck>();
        foreach (var entry in tenantEntries)
        {
            var householdId = entry.Property(nameof(ITenantEntity.HouseholdId));
            var current = (int)householdId.CurrentValue!;
            var original = (int)householdId.OriginalValue!;

            if (entry.State == EntityState.Added)
            {
                // Refused even inside AllowCrossTenantWrite: no sanctioned cross-write adds rows under a household
                // created in the same save (invite accept touches an existing invite and a non-tenant connection).
                if (householdId.IsTemporary)
                {
                    throw CrossTenantWriteException.UnderANewHousehold(EntityName(entry), tenantHh);
                }
                if (current != tenantHh && !crossTenantAllowed)
                {
                    throw CrossTenantWriteException.Mismatch(EntityName(entry), KeyText(entry), "Added", current, tenantHh);
                }
                continue;
            }

            if (entry.State == EntityState.Modified && (householdId.IsModified || original != current))
            {
                throw CrossTenantWriteException.Reassigned(EntityName(entry), KeyText(entry), original, current, tenantHh);
            }

            // Modified or Deleted: the row as loaded (or as a stub claims it) must be the tenant's.
            if (original != tenantHh && !crossTenantAllowed)
            {
                throw CrossTenantWriteException.Mismatch(EntityName(entry), KeyText(entry), entry.State.ToString(), original, tenantHh);
            }

            if (!crossTenantAllowed && !KeyIncludesHouseholdId(entry.Metadata))
            {
                if (!OwnershipQueries.TryGetValue(entry.Metadata.ClrType, out var ownership))
                {
                    throw new InvalidOperationException(
                        $"{EntityName(entry)} is a tenant entity whose primary key excludes HouseholdId, but it has no " +
                        "ownership query. Add it to ApplicationDbContext.OwnershipQueries (D9).");
                }
                var id = (int)entry.Property("Id").OriginalValue!;
                ownershipChecks.Add(new PendingOwnershipCheck(ownership, id, tenantHh, entry));
            }
        }
        return ownershipChecks;
    }

    internal static bool KeyIncludesHouseholdId(IReadOnlyEntityType type) =>
        type.FindPrimaryKey()!.Properties.Any(p => p.Name == nameof(ITenantEntity.HouseholdId));

    /// <summary>
    /// Does the row with this <c>Id</c> exist in the tenant's household? It runs under the Tenant filter, and it
    /// also names the household itself, so the check still holds while the read kill switch
    /// (<c>Tenancy:EnforceFilter=false</c>) is off. It ignores only <c>SoftDelete</c>, so an update to a soft-deleted
    /// own row isn't taken for a foreign one. It materializes nothing and doesn't run <c>DetectChanges</c>.
    /// <para><c>false</c> means ABSENT or FOREIGN, and the two need opposite answers, so a <c>false</c> is followed by
    /// <see cref="ExistsInAnyHousehold{TEntity}"/>: a row that exists in another household is refused
    /// (<see cref="CrossTenantWriteException"/>), and a row that exists nowhere, deleted concurrently, is left to base
    /// <c>SaveChanges</c>, which raises <c>DbUpdateConcurrencyException</c> as it did before the write step.</para>
    /// </summary>
    private bool Owned<TEntity>(int id, int householdId) where TEntity : class, ITenantEntity =>
        Set<TEntity>().IgnoreQueryFilters(["SoftDelete"])
            .Any(e => EF.Property<int>(e, "Id") == id && EF.Property<int>(e, nameof(ITenantEntity.HouseholdId)) == householdId);

    /// <inheritdoc cref="Owned{TEntity}"/>
    private Task<bool> OwnedAsync<TEntity>(int id, int householdId, CancellationToken cancellationToken) where TEntity : class, ITenantEntity =>
        Set<TEntity>().IgnoreQueryFilters(["SoftDelete"])
            .AnyAsync(e => EF.Property<int>(e, "Id") == id && EF.Property<int>(e, nameof(ITenantEntity.HouseholdId)) == householdId, cancellationToken);

    /// <summary>
    /// Does a row with this <c>Id</c> exist in ANY household? A boolean only, consulted only after
    /// <see cref="Owned{TEntity}"/> answered <c>false</c>, to tell a forged key (present elsewhere: refuse) from a
    /// concurrent delete (absent: let base raise its concurrency exception). It ignores <c>SoftDelete</c> too, so a
    /// soft-deleted foreign row still counts as present and is refused.
    /// </summary>
    private bool ExistsInAnyHousehold<TEntity>(int id) where TEntity : class, ITenantEntity
    {
        // TENANT-SCOPE-OK: returns a boolean only, consulted only after the tenant-scoped ownership query failed
        // (the gate at Data/ApplicationDbContext.Writes.cs:50); true refuses the write, false lets EF report the delete.
        return Set<TEntity>().IgnoreQueryFilters(["Tenant", "SoftDelete"]).Any(e => EF.Property<int>(e, "Id") == id);
    }

    /// <inheritdoc cref="ExistsInAnyHousehold{TEntity}"/>
    private async Task<bool> ExistsInAnyHouseholdAsync<TEntity>(int id, CancellationToken cancellationToken) where TEntity : class, ITenantEntity
    {
        // TENANT-SCOPE-OK: returns a boolean only, consulted only after the tenant-scoped ownership query failed
        // (the gate at Data/ApplicationDbContext.Writes.cs:62); true refuses the write, false lets EF report the delete.
        return await Set<TEntity>().IgnoreQueryFilters(["Tenant", "SoftDelete"]).AnyAsync(e => EF.Property<int>(e, "Id") == id, cancellationToken);
    }

    /// <summary>One surrogate-key type's ownership query and its any-household existence check, sync and async.</summary>
    internal sealed record OwnershipQuery(
        Func<ApplicationDbContext, int, int, bool> Sync,
        Func<ApplicationDbContext, int, int, CancellationToken, Task<bool>> Async,
        Func<ApplicationDbContext, int, bool> ExistsSync,
        Func<ApplicationDbContext, int, CancellationToken, Task<bool>> ExistsAsync)
    {
        public static OwnershipQuery For<TEntity>() where TEntity : class, ITenantEntity =>
            new((db, id, householdId) => db.Owned<TEntity>(id, householdId),
                (db, id, householdId, ct) => db.OwnedAsync<TEntity>(id, householdId, ct),
                (db, id) => db.ExistsInAnyHousehold<TEntity>(id),
                (db, id, ct) => db.ExistsInAnyHouseholdAsync<TEntity>(id, ct));
    }

    private sealed record PendingOwnershipCheck(OwnershipQuery Ownership, int Id, int TenantHouseholdId, EntityEntry Entry)
    {
        public CrossTenantWriteException NotOwned() =>
            CrossTenantWriteException.NotOwned(
                EntityName(Entry), KeyText(Entry), Entry.State.ToString(),
                (int)Entry.Property(nameof(ITenantEntity.HouseholdId)).OriginalValue!, TenantHouseholdId);
    }

    private static string EntityName(EntityEntry entry) => entry.Metadata.ClrType.Name;

    /// <summary>The key as the row stands: current values for an Added entry, original (as loaded) otherwise.</summary>
    private static string KeyText(EntityEntry entry) =>
        "{" + string.Join(", ", entry.Metadata.FindPrimaryKey()!.Properties
            .Select(p => $"{p.Name}={(entry.State == EntityState.Added ? entry.Property(p.Name).CurrentValue : entry.Property(p.Name).OriginalValue)}")) + "}";

    private TenantNotSetException WriteWithNoTenant()
    {
        var state = Tenant.IsOutOfRequest
            ? $"Unset outside any HTTP request, with Tenancy:OutOfRequest={Tenant.OutOfRequestMode}"
            : $"{Tenant.State} inside an HTTP request";
        return new TenantNotSetException(
            $"A tenant-scoped write ran with no tenant (the tenant is {state}). Mark the endpoint with " +
            "RequireTenant(), or wrap system work in context.Tenant.RunAs(householdId) through the save.");
    }
}
