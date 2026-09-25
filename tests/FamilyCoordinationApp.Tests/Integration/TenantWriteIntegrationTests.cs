using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Interfaces;
using FamilyCoordinationApp.Tenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    // ── A concurrent delete is not a forged row (amendment 1, item 2) ───────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_concurrent_delete_of_an_owned_surrogate_key_row_is_a_concurrency_conflict_not_a_forgery(bool sync)
    {
        int doomed;
        await using (var seed = await Unfiltered().CreateDbContextAsync())
        {
            var user = new User { HouseholdId = A, Email = $"race-{sync}@a.test", DisplayName = "Race", CreatedAt = DateTime.UtcNow };
            seed.Users.Add(user);
            await seed.SaveChangesAsync();
            doomed = user.Id;
        }

        var (scope, factory) = AsCaller(_factory, A, ChoresWebAppFactory.UserAId);
        using (scope)
        {
            await using var first = await factory.CreateDbContextAsync();
            var loaded = await first.Users.SingleAsync(u => u.Id == doomed);

            await using (var second = await factory.CreateDbContextAsync())
            {
                second.Users.Remove(await second.Users.SingleAsync(u => u.Id == doomed));
                await second.SaveChangesAsync();
            }

            // The row is gone from every household: the ownership query fails, the any-household check finds nothing,
            // and base SaveChanges reports 0 rows affected as before the write step (HouseholdMemberService.DeleteMemberAsync
            // turns that DbUpdateException into Blocked).
            first.Users.Remove(loaded);
            if (sync)
            {
                first.Invoking(d => d.SaveChanges()).Should().ThrowExactly<DbUpdateConcurrencyException>();
            }
            else
            {
                await first.Invoking(d => d.SaveChangesAsync()).Should().ThrowExactlyAsync<DbUpdateConcurrencyException>();
            }
        }
        LeaveRequest();
    }

    // ── The new-household refusal holds inside AllowCrossTenantWrite (amendment 1, item 3) ──

    [Fact]
    public async Task Inside_AllowCrossTenantWrite_rows_under_a_household_created_in_the_same_save_are_still_refused()
    {
        var (scope, factory) = AsCaller(_factory, A, ChoresWebAppFactory.UserAId);
        using (scope)
        {
            var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            await using var db = await factory.CreateDbContextAsync();
            var recipe = new Recipe { Household = new Household { Name = "new" }, RecipeId = 90, Name = "under a new household" };
            recipe.Ingredients.Add(new RecipeIngredient { RecipeId = 90, IngredientId = 1, Name = "salt" });
            db.Recipes.Add(recipe);

            using (tenant.AllowCrossTenantWrite("amendment 1, item 3: the scope does not admit a new household"))
            {
                await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowExactlyAsync<CrossTenantWriteException>()
                    .WithMessage("Added Recipe belongs to a household created in this same save*");
            }
        }
        LeaveRequest();

        await using var check = await Unfiltered().CreateDbContextAsync();
        (await check.Households.CountAsync()).Should().Be(2, "no household was created");
        (await check.Recipes.AnyAsync(r => r.RecipeId == 90)).Should().BeFalse();
        (await check.RecipeIngredients.AnyAsync(i => i.RecipeId == 90)).Should().BeFalse();
    }

    // ── A tenancy refusal is not swallowed by an endpoint's catch (amendment 1, item 1) ──

    /// <summary>
    /// <c>RoomsEndpoints.UpdateRoom</c> wraps <c>IRoomService.UpdateRoomAsync</c> in
    /// <c>catch (InvalidOperationException) → 404</c>, with no log line. The test host swaps in a decorator whose
    /// update, for a marker name, performs a genuine refused write (or a tenant query with no tenant) through the
    /// request's own services, and captures the host's logs with a test-only <see cref="ILoggerProvider"/> added
    /// through <c>ConfigureLogging</c>.
    /// </summary>
    [Theory]
    [InlineData(ForgingRoomService.ForgeCrossTenantWrite, typeof(CrossTenantWriteException))]
    [InlineData(ForgingRoomService.QueryWithNoTenant, typeof(TenantNotSetException))]
    public async Task A_tenancy_refusal_on_an_endpoint_that_catches_InvalidOperationException_answers_500_and_is_logged(
        string marker, Type expected)
    {
        var logs = new CapturingLoggerProvider();
        await using var host = _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureLogging(l => l.AddProvider(logs));
            b.ConfigureTestServices(services => services.AddScoped<IRoomService>(sp => new ForgingRoomService(
                ActivatorUtilities.CreateInstance<RoomService>(sp),
                sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
                sp.GetRequiredService<IServiceScopeFactory>())));
        });
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(ChoresWebAppFactory.TestUserHeader, ChoresWebAppFactory.UserAEmail);

        var response = await client.PutAsJsonAsync("/api/rooms/1", new { name = marker, icon = "x", photoPath = (string?)null });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError,
            "the handler's catch (InvalidOperationException) → 404 must not see a tenancy refusal; body: {0}", body);
        body.Should().Contain("\"message\"", "the /api exception branch answers JSON {message}");
        body.Should().NotContain(expected.Name, "nothing from the exception reaches the wire");

        var logged = logs.Entries.Where(e => e.Exception?.GetType() == expected).ToList();
        logged.Should().ContainSingle("the refusal is logged once, with the exception attached. Captured: {0}",
            string.Join(" | ", logs.Entries.Where(e => e.Level >= LogLevel.Warning)
                .Select(e => $"{e.Level} {e.Category}: {e.Message} [{e.Exception?.GetType().Name}]")));
        // Measured (amendment 1): the /api branch's ExceptionHandlerMiddleware logs it once, and the console formatter
        // (what `docker logs` shows) prints the exception's full type name on the line after this message.
        logged[0].Level.Should().Be(LogLevel.Error);
        logged[0].Category.Should().Be("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware");
        logged[0].Message.Should().Be("An unhandled exception has occurred while executing the request.");
        body.Should().Be("{\"message\":\"Something went wrong on our end.\"}");
    }

    /// <summary>A test-only room service: a marker name triggers a tenancy refusal; everything else is the real service.</summary>
    private sealed class ForgingRoomService(
        IRoomService inner, IDbContextFactory<ApplicationDbContext> dbFactory, IServiceScopeFactory scopes) : IRoomService
    {
        public const string ForgeCrossTenantWrite = "forge a cross-tenant write";
        public const string QueryWithNoTenant = "query with no tenant";

        public async Task<Room> UpdateRoomAsync(
            int householdId, int roomId, string name, string icon, string? photoPath, CancellationToken cancellationToken = default)
        {
            if (name == ForgeCrossTenantWrite)
            {
                // The request's Caller-bound factory: a Modified room of household B under Caller(A) reaches the write step.
                await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
                var foreign = new Room { HouseholdId = B, RoomId = roomId, Name = name, Icon = icon };
                db.Rooms.Attach(foreign);
                db.Entry(foreign).Property(r => r.Name).IsModified = true;
                await db.SaveChangesAsync(cancellationToken);
            }
            else if (name == QueryWithNoTenant)
            {
                // A fresh DI scope has its own Unset tenant, and it is still in-request (the HttpContext accessor flows
                // with the async context), so a tenant query throws TenantNotSetException, as the read filter does.
                using var scope = scopes.CreateScope();
                await using var db = await scope.ServiceProvider
                    .GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync(cancellationToken);
                await db.Rooms.CountAsync(cancellationToken);
            }
            return await inner.UpdateRoomAsync(householdId, roomId, name, icon, photoPath, cancellationToken);
        }

        public Task<List<Room>> ListRoomsAsync(int householdId, CancellationToken cancellationToken = default) =>
            inner.ListRoomsAsync(householdId, cancellationToken);

        public Task<Room?> GetRoomAsync(int householdId, int roomId, CancellationToken cancellationToken = default) =>
            inner.GetRoomAsync(householdId, roomId, cancellationToken);

        public Task<Room> CreateRoomAsync(int householdId, string name, string icon, string? photoPath = null, CancellationToken cancellationToken = default) =>
            inner.CreateRoomAsync(householdId, name, icon, photoPath, cancellationToken);

        public Task DeleteRoomAsync(int householdId, int roomId, CancellationToken cancellationToken = default) =>
            inner.DeleteRoomAsync(householdId, roomId, cancellationToken);

        public Task ReorderAsync(int householdId, IReadOnlyList<int> orderedRoomIds, CancellationToken cancellationToken = default) =>
            inner.ReorderAsync(householdId, orderedRoomIds, cancellationToken);
    }

    /// <summary>Records every log entry the host writes: category, level, formatted message and the exception.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public sealed record Entry(string Category, LogLevel Level, string Message, Exception? Exception);

        public ConcurrentQueue<Entry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

        public void Dispose() { }

        private sealed class Logger(string category, ConcurrentQueue<Entry> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                sink.Enqueue(new Entry(category, logLevel, formatter(state, exception), exception));
        }
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
