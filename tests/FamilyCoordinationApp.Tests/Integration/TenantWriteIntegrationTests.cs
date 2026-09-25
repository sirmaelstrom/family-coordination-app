using System.Security.Claims;
using System.Text.Json;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Tenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// The central write step through the real host against real Postgres (fca-household-scope WP-03, V7). The
/// surrogate-key ownership query and EF's key semantics (a temporary key for an unset <c>HouseholdId</c>) need the
/// relational provider. Every case runs in a real-host DI scope with an HttpContext set and the tenant set exactly as
/// the middleware sets it (E11), and reads back through <see cref="PostgresDbContextFactory"/> (Unfiltered).
/// <para>Negative controls (scratch, in the PR body): without the ownership query the forged <c>User</c> update
/// saves and Bob's row changes; without <c>LoginProfileService</c>'s <c>RunAs</c> the sign-in profile does not
/// persist.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class TenantWriteIntegrationTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private const int A = ChoresWebAppFactory.HouseholdAId, B = ChoresWebAppFactory.HouseholdBId;
    private readonly ChoresWebAppFactory _factory = new(postgres);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private PostgresDbContextFactory Unfiltered() => new(_factory.ConnectionString);

    /// <summary>A real-host request-shaped scope as household A's Alice, as <c>TenantFilterTests</c> builds it.</summary>
    private static (IServiceScope Scope, IDbContextFactory<ApplicationDbContext> Db) AsCaller(
        WebApplicationFactory<Program> host, int householdId, int userId)
    {
        var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext =
            new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetCaller(householdId, userId);
        return (scope, scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>());
    }

    private void LeaveRequest() => _factory.Services.GetRequiredService<IHttpContextAccessor>().HttpContext = null;

    private async Task<User> BobAsync()
    {
        await using var db = await Unfiltered().CreateDbContextAsync();
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == ChoresWebAppFactory.UserBId);
    }

    private async Task<int> SeedInviteAsync(int householdId, int createdByUserId, string code)
    {
        await using var db = await Unfiltered().CreateDbContextAsync();
        var invite = new HouseholdInvite
        {
            HouseholdId = householdId,
            InviteCode = code,
            CreatedByUserId = createdByUserId,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(1)
        };
        db.HouseholdInvites.Add(invite);
        await db.SaveChangesAsync();
        return invite.Id;
    }

    private async Task<bool> InviteExistsAsync(int id)
    {
        await using var db = await Unfiltered().CreateDbContextAsync();
        return await db.HouseholdInvites.AnyAsync(i => i.Id == id);
    }

    // ── The surrogate-key ownership check ───────────────────────────────────

    [Fact]
    public async Task A_forged_surrogate_key_update_throws_and_Bobs_row_is_unchanged()
    {
        var before = await BobAsync();

        var (scope, factory) = AsCaller(_factory, A, ChoresWebAppFactory.UserAId);
        using (scope)
        {
            await using var db = await factory.CreateDbContextAsync();
            var forged = new User { Id = ChoresWebAppFactory.UserBId, HouseholdId = A, DisplayName = "x" };
            db.Users.Attach(forged);
            db.Entry(forged).Property(u => u.DisplayName).IsModified = true;

            await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowExactlyAsync<CrossTenantWriteException>()
                .WithMessage($"Modified User {{Id={ChoresWebAppFactory.UserBId}}} claims household {A}, but no such row exists*");
        }
        LeaveRequest();

        (await BobAsync()).Should().BeEquivalentTo(before, "UPDATE … WHERE Id = @id never ran against Bob's row");
    }

    [Fact]
    public async Task A_forged_surrogate_key_delete_throws_and_the_row_survives()
    {
        var bInvite = await SeedInviteAsync(B, ChoresWebAppFactory.UserBId, "BBB234");

        var (scope, factory) = AsCaller(_factory, A, ChoresWebAppFactory.UserAId);
        using (scope)
        {
            // A stub claiming household A: only the ownership query can tell.
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.HouseholdInvites.Remove(new HouseholdInvite { Id = bInvite, HouseholdId = A });
                await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowExactlyAsync<CrossTenantWriteException>()
                    .WithMessage($"Deleted HouseholdInvite {{Id={bInvite}}} claims household {A}, but no such row exists*");
            }

            // A stub carrying its true household B: the mismatch check refuses it first.
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.HouseholdInvites.Remove(new HouseholdInvite { Id = bInvite, HouseholdId = B });
                await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowExactlyAsync<CrossTenantWriteException>()
                    .WithMessage($"Deleted HouseholdInvite {{Id={bInvite}}} belongs to household {B}, but the tenant is household {A}*");
            }
        }
        LeaveRequest();

        (await InviteExistsAsync(bInvite)).Should().BeTrue();
    }

    [Fact]
    public async Task Owned_surrogate_key_rows_update_and_delete_normally()
    {
        var aInvite = await SeedInviteAsync(A, ChoresWebAppFactory.UserAId, "AAA234");

        var (scope, factory) = AsCaller(_factory, A, ChoresWebAppFactory.UserAId);
        using (scope)
        {
            await using var db = await factory.CreateDbContextAsync();
            (await db.Users.SingleAsync(u => u.Id == ChoresWebAppFactory.UserAId)).DisplayName = "Alice Renamed";
            db.HouseholdInvites.Remove(await db.HouseholdInvites.SingleAsync(i => i.Id == aInvite));
            await db.SaveChangesAsync();
        }
        LeaveRequest();

        await using var check = await Unfiltered().CreateDbContextAsync();
        (await check.Users.SingleAsync(u => u.Id == ChoresWebAppFactory.UserAId)).DisplayName.Should().Be("Alice Renamed");
        (await InviteExistsAsync(aInvite)).Should().BeFalse();
    }

    [Fact]
    public async Task With_the_read_kill_switch_off_a_forged_surrogate_key_update_still_throws()
    {
        var before = await BobAsync();

        // EnforceFilter=false turns the Tenant filter off (D14), so the ownership query names the household itself.
        await using var host = _factory.WithWebHostBuilder(b => b.UseSetting("Tenancy:EnforceFilter", "false"));
        var (scope, factory) = AsCaller(host, A, ChoresWebAppFactory.UserAId);
        using (scope)
        {
            await using var db = await factory.CreateDbContextAsync();
            var forged = new User { Id = ChoresWebAppFactory.UserBId, HouseholdId = A, DisplayName = "x" };
            db.Users.Attach(forged);
            db.Entry(forged).Property(u => u.DisplayName).IsModified = true;

            await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowExactlyAsync<CrossTenantWriteException>();
        }
        LeaveRequest();

        (await BobAsync()).Should().BeEquivalentTo(before);
    }

    // ── Stamping, with Npgsql's temporary keys ──────────────────────────────

    [Fact]
    public async Task An_added_room_with_HouseholdId_0_saves_as_the_callers_household()
    {
        var (scope, factory) = AsCaller(_factory, B, ChoresWebAppFactory.UserBId);
        using (scope)
        {
            await using var db = await factory.CreateDbContextAsync();
            db.Rooms.Add(new Room { RoomId = 70, Name = "stamped", Icon = "s" });
            await db.SaveChangesAsync();
        }
        LeaveRequest();

        await using var check = await Unfiltered().CreateDbContextAsync();
        var room = await check.Rooms.SingleAsync(r => r.RoomId == 70);
        room.HouseholdId.Should().Be(B);
        room.CreatedAt.Should().NotBe(default, "D16 stamps an unset CreatedAt on insert");
    }

    [Fact]
    public async Task A_row_added_under_a_household_created_in_the_same_save_is_refused()
    {
        var (scope, factory) = AsCaller(_factory, A, ChoresWebAppFactory.UserAId);
        using (scope)
        {
            await using var db = await factory.CreateDbContextAsync();
            db.Rooms.Add(new Room { Household = new Household { Name = "new" }, RoomId = 71, Name = "r", Icon = "r" });

            await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowExactlyAsync<CrossTenantWriteException>()
                .WithMessage("Added Room belongs to a household created in this same save*");
        }
        LeaveRequest();

        await using var check = await Unfiltered().CreateDbContextAsync();
        (await check.Households.CountAsync()).Should().Be(2);
        (await check.Rooms.AnyAsync(r => r.RoomId == 71)).Should().BeFalse();
    }

    // ── The login outcome (moved here from WP-02 by council round 1) ────────

    /// <summary>
    /// <c>RefreshAsync</c> swallows every exception, so the proof is the OUTCOME (E11). The arrange is WP-02's
    /// <c>LoginProfileTenancyTests</c>: the OAuth <c>CreatingTicket</c> event, in-request, so the tenant is Unset exactly
    /// as in a real callback. With <c>RunAs(user.HouseholdId)</c> the save passes the write step and the profile
    /// persists; without it the save throws <see cref="TenantNotSetException"/> inside the catch and nothing persists.
    /// </summary>
    [Fact]
    public async Task The_sign_in_profile_write_persists_under_its_RunAs()
    {
        var connectionString = await postgres.CreateDatabaseConnectionStringAsync();
        await using var host = new DevAuthTestingWebAppFactory(connectionString);
        await host.MigrateAndSeedAsync();

        var unfiltered = new PostgresDbContextFactory(connectionString);
        await using (var seed = await unfiltered.CreateDbContextAsync())
        {
            seed.Users.Add(new User
            {
                HouseholdId = 1,
                Email = "write-step-signin@a.test",
                DisplayName = "Write Step",
                Initials = "",
                IsWhitelisted = true,
                CreatedAt = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        using var scope = host.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<GoogleOptions>>()
            .Get(GoogleDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Email, "write-step-signin@a.test"),
                new Claim("urn:google:picture", "https://pic.test/w.jpg"),
                new Claim(ClaimTypes.NameIdentifier, "google-subject-write-step")
            ],
            GoogleDefaults.AuthenticationScheme));

        using var backchannel = new HttpClient();
        using var empty = JsonDocument.Parse("{}");
        var context = new OAuthCreatingTicketContext(
            principal,
            new AuthenticationProperties(),
            new DefaultHttpContext { RequestServices = scope.ServiceProvider },
            new AuthenticationScheme(GoogleDefaults.AuthenticationScheme, null, typeof(GoogleHandler)),
            options,
            backchannel,
            OAuthTokenResponse.Success(empty),
            empty.RootElement);

        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = context.HttpContext; // in-request: the tenant is Unset, as in a real callback
        try
        {
            await options.Events.CreatingTicket(context);
        }
        finally
        {
            accessor.HttpContext = null;
        }

        await using var db = await unfiltered.CreateDbContextAsync();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Email == "write-step-signin@a.test");
        user.PictureUrl.Should().Be("https://pic.test/w.jpg", "the profile save runs inside RunAs(user.HouseholdId), so the write step passes it");
        user.GoogleId.Should().Be("google-subject-write-step");
        user.Initials.Should().Be("WS");
        user.LastLoginAt.Should().NotBeNull();
    }
}
