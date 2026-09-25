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
/// <item>Every Added, Modified or Deleted <see cref="ITenantEntity"/> is checked against the tenant: an Added
/// <c>HouseholdId == 0</c> is stamped (parents before children); a row of another household throws
/// <see cref="CrossTenantWriteException"/> unless inside <c>AllowCrossTenantWrite</c>; a changed <c>HouseholdId</c>
/// always throws; an Unset tenant throws <see cref="TenantNotSetException"/>.</item>
/// <item>A Modified or Deleted row whose primary key excludes <c>HouseholdId</c> (<c>User</c>,
/// <c>HouseholdInvite</c>, <c>HouseholdCalendarToken</c>) must exist in the tenant's household, because its
/// <c>UPDATE … WHERE Id = @id</c> would otherwise reach a row whose tracked <c>HouseholdId</c> was forged.</item>
/// </list>
/// The tenant checks are skipped with <c>Tenancy:EnforceWrites=false</c> (the D14 kill switch) and in the bypass
/// states (the options-only Unfiltered constructor, or out of request with <c>OutOfRequest=Unfiltered</c>), which
/// production cannot reach. <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> never pass through here: the read filter scopes
/// the rows they select, and <c>TenantWriteArchitectureTests</c> bans a <c>SetProperty</c> that assigns
/// <c>HouseholdId</c>.
/// </summary>
public partial class ApplicationDbContext
{
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        foreach (var check in PrepareWrites())
        {
            if (!check.Ownership.Sync(this, check.Id, check.TenantHouseholdId))
            {
                throw check.NotOwned();
            }
        }
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        foreach (var check in PrepareWrites())
        {
            if (!await check.Ownership.Async(this, check.Id, check.TenantHouseholdId, cancellationToken))
            {
                throw check.NotOwned();
            }
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
    /// <c>Household</c> principal tracked in this save (a household being created now), which is set by the code and
    /// is never re-pointed at the tenant: <see cref="Validate"/> refuses it as a mismatch instead.</para>
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
                if (householdId.IsTemporary && !crossTenantAllowed)
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
    /// </summary>
    private bool Owned<TEntity>(int id, int householdId) where TEntity : class, ITenantEntity =>
        Set<TEntity>().IgnoreQueryFilters(["SoftDelete"])
            .Any(e => EF.Property<int>(e, "Id") == id && EF.Property<int>(e, nameof(ITenantEntity.HouseholdId)) == householdId);

    /// <inheritdoc cref="Owned{TEntity}"/>
    private Task<bool> OwnedAsync<TEntity>(int id, int householdId, CancellationToken cancellationToken) where TEntity : class, ITenantEntity =>
        Set<TEntity>().IgnoreQueryFilters(["SoftDelete"])
            .AnyAsync(e => EF.Property<int>(e, "Id") == id && EF.Property<int>(e, nameof(ITenantEntity.HouseholdId)) == householdId, cancellationToken);

    /// <summary>One surrogate-key type's ownership query, in its sync and async forms.</summary>
    internal sealed record OwnershipQuery(
        Func<ApplicationDbContext, int, int, bool> Sync,
        Func<ApplicationDbContext, int, int, CancellationToken, Task<bool>> Async)
    {
        public static OwnershipQuery For<TEntity>() where TEntity : class, ITenantEntity =>
            new((db, id, householdId) => db.Owned<TEntity>(id, householdId),
                (db, id, householdId, ct) => db.OwnedAsync<TEntity>(id, householdId, ct));
    }

    private sealed record PendingOwnershipCheck(OwnershipQuery Ownership, int Id, int TenantHouseholdId, EntityEntry Entry)
    {
        public CrossTenantWriteException NotOwned() =>
            CrossTenantWriteException.NotOwned(
                EntityName(Entry), KeyText(Entry), Entry.State.ToString(),
                (int)Entry.Property(nameof(ITenantEntity.HouseholdId)).OriginalValue!, TenantHouseholdId);
    }

    private static string EntityName(EntityEntry entry) => entry.Metadata.ClrType.Name;

    private static string KeyText(EntityEntry entry) =>
        "{" + string.Join(", ", entry.Metadata.FindPrimaryKey()!.Properties
            .Select(p => $"{p.Name}={entry.Property(p.Name).OriginalValue}")) + "}";

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
