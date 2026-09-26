using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Tenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FamilyCoordinationApp.Tests.Tenancy;

/// <summary>
/// The write step on a shape no real entity has today (amendment 2, items 1 and 2): a test-only context adds a probe
/// tenant entity with a surrogate key, a foreign key to <see cref="Household"/> and NO navigation, and no ownership
/// query. The real model, its snapshot and the migrations are untouched (MN7); the probe lives only in this context's
/// own model (EF caches models per context type).
/// <list type="bullet">
/// <item>On every real tenant entity EF's fixup sets the back-navigation from the foreign key alone (measured in
/// <c>TenantWriteIntegrationTests.A_row_naming_a_new_household_only_by_its_foreign_key_is_refused_inside_AllowCrossTenantWrite</c>),
/// so only the probe can show that the new-household refusal is by key, not by navigation.</item>
/// <item>No real surrogate-key tenant type lacks an ownership query (the dispatch-set test holds that), so only the
/// probe reaches the fail-closed throw for a missing one.</item>
/// </list>
/// </summary>
public class TenantWriteProbeTests
{
    private readonly string _store = $"tenant-write-probe-{Guid.NewGuid():N}";

    private ProbeContext Context(TenantContext tenant) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(_store).Options, tenant);

    private static TenantContext Caller(int householdId)
    {
        var tenant = new TenantContext(new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            Microsoft.Extensions.Options.Options.Create(new TenancyOptions()));
        tenant.SetCaller(householdId, userId: 1);
        return tenant;
    }

    [Fact]
    public async Task A_navigation_less_row_under_a_household_added_in_the_same_save_is_refused_inside_AllowCrossTenantWrite()
    {
        var tenant = Caller(1);
        await using var db = Context(tenant);
        db.Households.Add(new Household { Id = 9003, Name = "explicit id" });
        var row = new ProbeRow { Id = 1, HouseholdId = 9003, Name = "fk only" };
        db.Add(row);

        db.Entry(row).Metadata.GetForeignKeys().Should().ContainSingle()
            .Which.DependentToPrincipal.Should().BeNull("the probe has no navigation for a detector to read");

        using (tenant.AllowCrossTenantWrite("amendment 2, item 1b: a plain mismatch can't be what refuses it"))
        {
            await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowExactlyAsync<CrossTenantWriteException>()
                .WithMessage("Added ProbeRow {Id=1} belongs to a household created in this same save (household 9003), " +
                             "but the tenant is household 1*");
        }
    }

    [Fact]
    public async Task A_modified_surrogate_key_tenant_row_with_no_ownership_query_is_refused_as_a_tenancy_exception()
    {
        await using var db = Context(Caller(1));
        var row = new ProbeRow { Id = 1, HouseholdId = 1, Name = "x" };
        db.Attach(row);
        db.Entry(row).Property(r => r.Name).IsModified = true;

        // Not an InvalidOperationException: an endpoint's catch (InvalidOperationException) → 404 must not hide it.
        await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowExactlyAsync<CrossTenantWriteException>()
            .WithMessage("Modified ProbeRow {Id=1} claims household 1 (the tenant is household 1), but its type's primary key " +
                         "excludes HouseholdId and it has no ownership query*");
    }

    /// <summary>A tenant entity keyed on <c>Id</c> alone, with a foreign key to <see cref="Household"/> and no navigation.</summary>
    private sealed class ProbeRow : ITenantEntity
    {
        public int Id { get; set; }
        public int HouseholdId { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class ProbeContext(DbContextOptions<ApplicationDbContext> options, ITenantContext tenant)
        : ApplicationDbContext(options, tenant, new TenancyOptions())
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ProbeRow>(b =>
            {
                b.HasKey(r => r.Id);
                b.HasOne<Household>().WithMany().HasForeignKey(r => r.HouseholdId);
            });
            base.OnModelCreating(modelBuilder); // after the probe, so it gets the Tenant filter like a real tenant type
        }
    }
}
