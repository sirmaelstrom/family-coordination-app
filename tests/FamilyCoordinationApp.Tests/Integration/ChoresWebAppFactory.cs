using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// Boots the real <see cref="Program"/> host (full DI + middleware + endpoint pipeline) against the
/// Testcontainers PostgreSQL from <see cref="PostgresContainerFixture"/>, so integration tests hit the
/// production <c>/api/chores</c> + <c>/api/rooms</c> endpoints, the real <c>IChoreService</c> save path, and
/// the real <c>xmin</c> concurrency token end-to-end. The InMemory provider cannot do this — that is the
/// whole point of the harness (operator decision on VG1, M7/M12).
/// <para><b>Auth:</b> Google OAuth is replaced by a header-driven <see cref="TestAuthHandler"/> set as the
/// default scheme. A request carrying <c>X-Test-User: &lt;email&gt;</c> is authenticated as that email; the
/// production <c>UserContextResolver.ResolveUserAsync</c> + <c>WhitelistedEmailHandler</c> then resolve the
/// real <c>{HouseholdId, UserId}</c> from the seeded user row (M1 invariant preserved — HouseholdId is never
/// client-supplied, it comes from the resolved principal). A request with no header is unauthenticated → the
/// endpoints' <c>.RequireAuthorization()</c> yields 401.</para>
/// <para><b>Config:</b> dummy Google ClientId/ClientSecret are injected so the real <c>Program.cs</c> startup
/// (which throws without them) boots; the environment is set to <c>Testing</c> (not Production → no
/// DataProtection cert required; not Development → the dev seed path does not fire).</para>
/// <para><b>Seed:</b> after migration the factory seeds two DISTINCT households, each with a whitelisted user
/// and a known unclaimed pile chore (<see cref="AssignmentKind.None"/>), giving a deterministic fixture for
/// both the concurrency race and the cross-household isolation test.</para>
/// </summary>
public sealed class ChoresWebAppFactory(PostgresContainerFixture postgres) : WebApplicationFactory<Program>
{
    /// <summary>
    /// Why the HTTP-host-level integration tests are currently skipped (see the WP-08 findings). The HTTP host
    /// cannot start due to TWO pre-existing production defects that are OUT OF WP-08's boundary to fix:
    /// (1) <c>ChoresEndpoints.DeleteChore</c> uses <c>MapDelete</c> with an inferred request body, which .NET
    /// rejects at endpoint build ("Body was inferred but the method does not allow inferred body parameters");
    /// (2) the orphaned <c>20260131232149_AddShoppingListFavorites</c> migration (no <c>.Designer.cs</c>, not
    /// registered in the migration chain) makes a fresh-DB <c>MigrateAsync</c> fail dropping a never-created
    /// index. The mandated xmin→409 concurrency claim IS verified against real Postgres at the service layer in
    /// <see cref="ChoreServiceConcurrencyTests"/>. Remove these Skips once the two production defects are fixed
    /// (the harness here is complete and ready to exercise the endpoints end-to-end).
    /// </summary>
    public const string HostBlockedSkip =
        "Blocked by two pre-existing production defects reported in the WP-08 findings (out of WP-08 boundary " +
        "to fix): (1) ChoresEndpoints.DeleteChore MapDelete-with-inferred-body breaks host startup; " +
        "(2) orphaned 20260131232149_AddShoppingListFavorites migration breaks fresh-DB MigrateAsync. The " +
        "xmin->409 claim is verified against real Postgres at the service layer in ChoreServiceConcurrencyTests.";

    /// <summary>The header the <see cref="TestAuthHandler"/> reads to pick the active identity per request.</summary>
    public const string TestUserHeader = "X-Test-User";

    public const string TestAuthScheme = "Test";

    // ── Seeded fixture identities (deterministic — see SeedAsync) ────────────────────────────────
    public const int HouseholdAId = 1;
    public const int HouseholdBId = 2;

    public const string UserAEmail = "alice@household-a.test";
    public const string UserA2Email = "amy@household-a.test"; // second member of household A (for the race)
    public const string UserBEmail = "bob@household-b.test";

    public const int UserAId = 1;
    public const int UserA2Id = 2;
    public const int UserBId = 3;

    // A pile chore (AssignmentKind.None) in household A — the concurrency-race target.
    public const int PileChoreAId = 1;

    // A chore in household B — the cross-household isolation target (must be 404 for a household-A caller).
    public const int ChoreBId = 1;

    private bool _seeded;
    private readonly SemaphoreSlim _seedLock = new(1, 1);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.UseSetting("ConnectionStrings:DefaultConnection", postgres.ConnectionString);
        // Satisfy the mandatory Google OAuth config keys so Program.cs does not throw at startup (council C3).
        builder.UseSetting("Authentication:Google:ClientId", "test-client-id");
        builder.UseSetting("Authentication:Google:ClientSecret", "test-client-secret");

        builder.ConfigureServices(services =>
        {
            // Replace the cookie+Google auth with a single header-driven Test scheme as the default for both
            // authenticate and challenge. The endpoints' default authorization policy still runs
            // (RequireAuthenticatedUser + WhitelistedEmailRequirement), so the seeded users must be whitelisted.
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthScheme;
                options.DefaultChallengeScheme = TestAuthScheme;
                options.DefaultScheme = TestAuthScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthScheme, _ => { });
        });
    }

    /// <summary>
    /// Ensure the schema is migrated (proving the WP-01 additive migration applies cleanly on real Postgres)
    /// and the deterministic fixture is seeded, exactly once per container/host. Idempotent and thread-safe.
    /// </summary>
    public async Task EnsureSeededAsync()
    {
        if (_seeded) return;
        await _seedLock.WaitAsync();
        try
        {
            if (_seeded) return;

            var dbFactory = Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            await using var context = await dbFactory.CreateDbContextAsync();

            // Apply migrations on the real Postgres. If the WP-01 migration is non-additive or breaks on PG,
            // this throws and the suite fails loudly (a real-bug signal, not something to patch here).
            await context.Database.MigrateAsync();

            await SeedAsync(context);
            _seeded = true;
        }
        finally
        {
            _seedLock.Release();
        }
    }

    private static async Task SeedAsync(ApplicationDbContext context)
    {
        // Clean-slate, deterministic seed inserted directly via the context (NOT via SetupService, which would
        // pull in WP-07's 11-chore library and obscure the fixture). Two distinct households.
        if (await context.Households.AnyAsync()) return; // idempotent guard

        var now = DateTime.UtcNow;

        var householdA = new Household { Id = HouseholdAId, Name = "Household A", CreatedAt = now };
        var householdB = new Household { Id = HouseholdBId, Name = "Household B", CreatedAt = now };
        context.Households.AddRange(householdA, householdB);

        context.Users.AddRange(
            new User
            {
                Id = UserAId,
                HouseholdId = HouseholdAId,
                Email = UserAEmail,
                DisplayName = "Alice A",
                Initials = "AA",
                IsWhitelisted = true,
                CreatedAt = now
            },
            new User
            {
                Id = UserA2Id,
                HouseholdId = HouseholdAId,
                Email = UserA2Email,
                DisplayName = "Amy A",
                Initials = "AA",
                IsWhitelisted = true,
                CreatedAt = now
            },
            new User
            {
                Id = UserBId,
                HouseholdId = HouseholdBId,
                Email = UserBEmail,
                DisplayName = "Bob B",
                Initials = "BB",
                IsWhitelisted = true,
                CreatedAt = now
            });

        // Household A: an unclaimed pile chore (the concurrency-race target). Flexible so it never terminates.
        context.Chores.Add(new Chore
        {
            HouseholdId = HouseholdAId,
            ChoreId = PileChoreAId,
            Name = "Pile chore (race target)",
            RecurrenceMode = RecurrenceMode.Flexible,
            IntervalDays = 7,
            EffortTier = EffortTier.Standard,
            EffortPoints = ChoreEffort.PointsFor(EffortTier.Standard),
            Status = ChoreStatus.Active,
            EnteredByUserId = UserAId,
            AssigneeUserId = null,
            AssignmentKind = AssignmentKind.None,
            ClaimedAt = null,
            CreatedAt = now
        });

        // Household B: a chore that household-A callers must never see (cross-household isolation, M1).
        context.Chores.Add(new Chore
        {
            HouseholdId = HouseholdBId,
            ChoreId = ChoreBId,
            Name = "Household B private chore",
            RecurrenceMode = RecurrenceMode.OneOff,
            EffortTier = EffortTier.Quick,
            EffortPoints = ChoreEffort.PointsFor(EffortTier.Quick),
            Status = ChoreStatus.Active,
            EnteredByUserId = UserBId,
            AssignmentKind = AssignmentKind.None,
            CreatedAt = now
        });

        await context.SaveChangesAsync();
    }

    /// <summary>Create an <see cref="HttpClient"/> that authenticates every request as the given user email.</summary>
    public HttpClient CreateClientAs(string email)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false // a 302 (e.g. setup/login redirect) must surface, not be followed
        });
        client.DefaultRequestHeaders.Add(TestUserHeader, email);
        return client;
    }

    /// <summary>Create an unauthenticated <see cref="HttpClient"/> (no identity header) for 401 assertions.</summary>
    public HttpClient CreateAnonymousClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false
    });
}

/// <summary>
/// Test authentication handler: authenticates a request as the email in the <c>X-Test-User</c> header by
/// emitting a <see cref="ClaimTypes.Email"/> + <see cref="ClaimTypes.Name"/> claim. The production
/// <c>UserContextResolver.ResolveUserAsync</c> + <c>WhitelistedEmailHandler</c> then resolve the real
/// household/user from the database — so the M1 "HouseholdId comes only from the resolved principal"
/// invariant is exercised, not bypassed. No header → <see cref="AuthenticateResult.NoResult"/> → 401.
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ChoresWebAppFactory.TestUserHeader, out var values))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var email = values.ToString();
        if (string.IsNullOrWhiteSpace(email))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Name, email),
            new Claim(ClaimTypes.NameIdentifier, email)
        };
        var identity = new ClaimsIdentity(claims, ChoresWebAppFactory.TestAuthScheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, ChoresWebAppFactory.TestAuthScheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
