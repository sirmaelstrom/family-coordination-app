using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Tenancy;
using FamilyCoordinationApp.Tests.Integration;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace FamilyCoordinationApp.Tests.Tenancy;

/// <summary>
/// The E1 spike (fca-household-scope WP-02 step 1), kept as the mechanism's regression pin. A scratch context with two
/// tiny entities carries the SAME filter the app ships: <c>HasQueryFilter("Tenant", e => BypassTenantFilter ||
/// e.HouseholdId == CurrentHouseholdId)</c>, built by <see cref="ApplicationDbContext.ApplyTenantFilter"/>, whose two
/// instance properties delegate to <see cref="ApplicationDbContext.BypassTenantFilterFor"/> and
/// <see cref="ApplicationDbContext.CurrentHouseholdIdFor"/>, over the real <see cref="TenantContext"/> and a
/// <see cref="TenancySettings"/> built from <see cref="TenancyOptions"/>.
/// <para>It proves, per provider: (a) an Unset tenant throws <see cref="TenantNotSetException"/>, EF re-reads the
/// properties on every execution (two tenants, two results), and a bypassing context never throws; (b) the named
/// <c>"Tenant"</c> and <c>"SoftDelete"</c> filters compose, and ignoring one keeps the other. Npgsql only: (c) the
/// filter reaches <c>Include</c>, <c>ExecuteUpdateAsync</c> and <c>ExecuteDeleteAsync</c>, and shows in
/// <c>ToQueryString()</c>. InMemory has no bulk operations, so (c) there would be a false E1.</para>
/// </summary>
public abstract class TenantFilterSpikeCases
{
    // Rooms: h1 has 2 live + 1 soft-deleted; h2 has 1 live + 1 soft-deleted. Items: h1 has 2 (room 1); h2 has 1
    // pointing at h1's room 1 (a single-column FK, so a foreign-household child is representable).
    internal const int H1LiveRooms = 2, H1AllRooms = 3, H2LiveRooms = 1, H2AllRooms = 2;

    protected abstract DbContextOptions<SpikeContext> Options { get; }

    internal static TenantContext InRequest() =>
        new(new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, Microsoft.Extensions.Options.Options.Create(new TenancyOptions()));

    internal static TenantContext OutOfRequest(OutOfRequestMode mode) =>
        new(new HttpContextAccessor(), Microsoft.Extensions.Options.Options.Create(new TenancyOptions { OutOfRequest = mode }));

    internal static TenantContext Caller(int householdId)
    {
        var tenant = InRequest();
        tenant.SetCaller(householdId, householdId);
        return tenant;
    }

    internal SpikeContext NewContext(ITenantContext tenant, bool enforceFilter = true) =>
        new(Options, tenant, new TenancyOptions { EnforceFilter = enforceFilter });

    internal static void Seed(SpikeContext db)
    {
        db.Rooms.AddRange(
            new SpikeRoom { Id = 1, HouseholdId = 1, Name = "h1-a" },
            new SpikeRoom { Id = 2, HouseholdId = 1, Name = "h1-b" },
            new SpikeRoom { Id = 3, HouseholdId = 1, Name = "h1-deleted", IsDeleted = true },
            new SpikeRoom { Id = 4, HouseholdId = 2, Name = "h2-a" },
            new SpikeRoom { Id = 5, HouseholdId = 2, Name = "h2-deleted", IsDeleted = true });
        db.Items.AddRange(
            new SpikeItem { Id = 1, HouseholdId = 1, RoomId = 1 },
            new SpikeItem { Id = 2, HouseholdId = 1, RoomId = 1 },
            new SpikeItem { Id = 3, HouseholdId = 2, RoomId = 1 });
        db.SaveChanges();
    }

    /// <summary>
    /// Asserts the query throws <see cref="TenantNotSetException"/> itself, not an EF wrapper around it, so callers
    /// and logs see the tenant state (V4).
    /// </summary>
    internal static async Task ShouldThrowTenantNotSet(Func<Task> query) =>
        await query.Should().ThrowExactlyAsync<TenantNotSetException>();

    // ── (a) the getter throws when Unset; EF re-reads the tenant per execution; bypass never throws ──

    [Fact]
    public async Task A_Unset_tenant_in_request_throws_TenantNotSetException()
    {
        await using var db = NewContext(InRequest());

        await ShouldThrowTenantNotSet(() => db.Rooms.ToListAsync());
    }

    [Fact]
    public async Task A_Unset_tenant_out_of_request_with_Throw_throws()
    {
        await using var db = NewContext(OutOfRequest(OutOfRequestMode.Throw));

        await ShouldThrowTenantNotSet(() => db.Rooms.ToListAsync());
    }

    [Fact]
    public async Task A_two_contexts_with_two_tenants_see_two_different_results()
    {
        await using var one = NewContext(Caller(1));
        await using var two = NewContext(Caller(2));

        (await one.Rooms.Select(r => r.HouseholdId).ToListAsync()).Should().HaveCount(H1LiveRooms).And.OnlyContain(h => h == 1);
        (await two.Rooms.Select(r => r.HouseholdId).ToListAsync()).Should().HaveCount(H2LiveRooms).And.OnlyContain(h => h == 2);
        (await one.Rooms.CountAsync()).Should().Be(H1LiveRooms, "the first context is not rebound by the second's query");
    }

    [Fact]
    public async Task A_one_context_rereads_the_tenant_on_every_execution()
    {
        var tenant = Caller(1);
        await using var db = NewContext(tenant);

        (await db.Rooms.CountAsync()).Should().Be(H1LiveRooms);
        using (tenant.RunAs(2))
        {
            (await db.Rooms.CountAsync()).Should().Be(H2LiveRooms, "RunAs(2) takes effect on the next execution");
        }
        (await db.Rooms.CountAsync()).Should().Be(H1LiveRooms, "disposing RunAs restores Caller(1)");
    }

    [Fact]
    public async Task A_kill_switch_off_with_the_tenant_Unset_returns_everything_and_does_not_throw()
    {
        await using var db = NewContext(InRequest(), enforceFilter: false);

        (await db.Rooms.CountAsync()).Should().Be(H1LiveRooms + H2LiveRooms);
    }

    [Fact]
    public async Task A_an_Unfiltered_tenant_returns_everything_and_does_not_throw()
    {
        await using var db = NewContext(TenantContext.CreateUnfiltered());

        (await db.Rooms.CountAsync()).Should().Be(H1LiveRooms + H2LiveRooms);
    }

    [Fact]
    public async Task A_out_of_request_with_Unfiltered_returns_everything_and_does_not_throw()
    {
        await using var db = NewContext(OutOfRequest(OutOfRequestMode.Unfiltered));

        (await db.Rooms.CountAsync()).Should().Be(H1LiveRooms + H2LiveRooms);
    }

    // ── (b) the named filters compose ────────────────────────────────────────

    [Fact]
    public async Task B_both_filters_apply_by_default()
    {
        await using var db = NewContext(Caller(1));

        (await db.Rooms.Select(r => r.Name).ToListAsync()).Should().BeEquivalentTo(["h1-a", "h1-b"]);
    }

    [Fact]
    public async Task B_ignoring_SoftDelete_keeps_Tenant_in_force()
    {
        await using var db = NewContext(Caller(1));

        (await db.Rooms.IgnoreQueryFilters(["SoftDelete"]).Select(r => r.Name).ToListAsync())
            .Should().BeEquivalentTo(["h1-a", "h1-b", "h1-deleted"]);
    }

    [Fact]
    public async Task B_ignoring_Tenant_keeps_SoftDelete_in_force_and_does_not_throw_when_Unset()
    {
        await using var db = NewContext(InRequest());

        (await db.Rooms.IgnoreQueryFilters(["Tenant"]).Select(r => r.Name).ToListAsync())
            .Should().BeEquivalentTo(["h1-a", "h1-b", "h2-a"]);
    }

    [Fact]
    public async Task B_ignoring_both_returns_every_row()
    {
        await using var db = NewContext(InRequest());

        (await db.Rooms.IgnoreQueryFilters(["Tenant", "SoftDelete"]).CountAsync()).Should().Be(H1AllRooms + H2AllRooms);
    }
}

/// <summary>(a) and (b) on the InMemory provider, the one the unit tests use.</summary>
public sealed class TenantFilterSpikeInMemoryTests : TenantFilterSpikeCases
{
    public TenantFilterSpikeInMemoryTests()
    {
        Options = new DbContextOptionsBuilder<SpikeContext>()
            .UseInMemoryDatabase($"tenant-spike-{Guid.NewGuid():N}")
            .Options;
        using var db = new SpikeContext(Options, TenantContext.CreateUnfiltered(), new TenancyOptions());
        Seed(db);
    }

    protected override DbContextOptions<SpikeContext> Options { get; }
}

/// <summary>(a) and (b) on real Postgres, plus (c): Include, ExecuteUpdate, ExecuteDelete and the SQL text.</summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class TenantFilterSpikeNpgsqlTests(PostgresContainerFixture postgres) : TenantFilterSpikeCases, IAsyncLifetime
{
    private DbContextOptions<SpikeContext>? _options;

    protected override DbContextOptions<SpikeContext> Options =>
        _options ?? throw new InvalidOperationException("InitializeAsync has not run.");

    public async Task InitializeAsync()
    {
        _options = new DbContextOptionsBuilder<SpikeContext>()
            .UseNpgsql(await postgres.CreateDatabaseConnectionStringAsync())
            .Options;
        await using var db = new SpikeContext(_options, TenantContext.CreateUnfiltered(), new TenancyOptions());
        await db.Database.EnsureCreatedAsync();
        Seed(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task C_the_filter_reaches_Include_navigations()
    {
        await using var db = NewContext(Caller(1));

        var room = await db.Rooms.Include(r => r.Items).SingleAsync(r => r.Id == 1);

        room.Items.Should().HaveCount(2).And.OnlyContain(i => i.HouseholdId == 1, "h2's item points at this room but is filtered out");
    }

    [Fact]
    public async Task C_the_filter_reaches_ExecuteUpdateAsync()
    {
        await using var db = NewContext(Caller(1));

        // Sets Name to itself, so the seed is unchanged; the affected-row count shows which rows were selected.
        var affected = await db.Rooms.IgnoreQueryFilters(["SoftDelete"]).ExecuteUpdateAsync(s => s.SetProperty(r => r.Name, r => r.Name));

        affected.Should().Be(H1AllRooms);
    }

    [Fact]
    public async Task C_the_filter_reaches_ExecuteDeleteAsync()
    {
        await using (var seed = NewContext(TenantContext.CreateUnfiltered()))
        {
            seed.Items.AddRange(
                new SpikeItem { Id = 101, HouseholdId = 1, RoomId = 2 },
                new SpikeItem { Id = 102, HouseholdId = 2, RoomId = 4 });
            await seed.SaveChangesAsync();
        }

        await using (var db = NewContext(Caller(1)))
        {
            (await db.Items.Where(i => i.Id > 100).ExecuteDeleteAsync()).Should().Be(1);
        }

        await using var check = NewContext(TenantContext.CreateUnfiltered());
        (await check.Items.Where(i => i.Id > 100).Select(i => i.Id).ToListAsync()).Should().Equal(102);
    }

    [Fact]
    public async Task C_an_Unset_ExecuteUpdateAsync_throws_instead_of_updating_everything()
    {
        await using var db = NewContext(InRequest());

        await ShouldThrowTenantNotSet(() => db.Rooms.ExecuteUpdateAsync(s => s.SetProperty(r => r.Name, r => r.Name)));
    }

    [Fact]
    public async Task C_the_SQL_carries_the_tenant_predicate_for_the_root_and_the_include()
    {
        await using var db = NewContext(Caller(1));

        var sql = db.Rooms.Include(r => r.Items).ToQueryString();

        sql.Should().Contain("FROM \"Rooms\"").And.Contain("FROM \"Items\"");
        System.Text.RegularExpressions.Regex.Matches(sql, "\"HouseholdId\" = @").Should()
            .HaveCount(2, $"one tenant predicate for the root and one for the included navigation:\n{sql}");
    }
}

// ── The scratch model ───────────────────────────────────────────────────────

public sealed class SpikeRoom : ITenantEntity
{
    public int Id { get; set; }
    public int HouseholdId { get; set; }
    public string Name { get; set; } = "";
    public bool IsDeleted { get; set; }
    public List<SpikeItem> Items { get; set; } = [];
}

public sealed class SpikeItem : ITenantEntity
{
    public int Id { get; set; }
    public int HouseholdId { get; set; }
    public int RoomId { get; set; }
}

/// <summary>
/// The scratch context. Its Tenant filter comes from the app's own builder,
/// <see cref="ApplicationDbContext.ApplyTenantFilter"/>, over this context's two same-named properties.
/// </summary>
public sealed class SpikeContext : DbContext
{
    public SpikeContext(DbContextOptions<SpikeContext> options, ITenantContext tenant, TenancyOptions tenancy) : base(options)
    {
        Tenant = tenant;
        Tenancy = new TenancySettings(tenancy);
    }

    internal ITenantContext Tenant { get; }

    internal TenancySettings Tenancy { get; }

    internal bool BypassTenantFilter => ApplicationDbContext.BypassTenantFilterFor(Tenant, Tenancy);

    internal int CurrentHouseholdId => ApplicationDbContext.CurrentHouseholdIdFor(Tenant, Tenancy);

    public DbSet<SpikeRoom> Rooms => Set<SpikeRoom>();

    public DbSet<SpikeItem> Items => Set<SpikeItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SpikeRoom>().Property(r => r.Id).ValueGeneratedNever();
        modelBuilder.Entity<SpikeItem>().Property(i => i.Id).ValueGeneratedNever();
        modelBuilder.Entity<SpikeRoom>().HasMany(r => r.Items).WithOne().HasForeignKey(i => i.RoomId);
        modelBuilder.Entity<SpikeRoom>().HasQueryFilter("SoftDelete", r => !r.IsDeleted);

        ApplicationDbContext.ApplyTenantFilter(modelBuilder, this);
    }
}
