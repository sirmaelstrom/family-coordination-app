using System.Security.Claims;
using System.Text.Encodings.Web;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Digest;
using FamilyCoordinationApp.Services.Interfaces;
using FamilyCoordinationApp.Tests.Fakes;
using FamilyCoordinationApp.Tests.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
public class ChoresWebAppFactory(PostgresContainerFixture postgres) : WebApplicationFactory<Program>
{
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

    // ── Digest fixture (v1.1 WP-08) ──────────────────────────────────────────────────────────────
    // The shared-secret token the digest-run endpoint requires via the X-Digest-Trigger-Token header.
    public const string DigestTriggerToken = "test-digest-trigger-token-7f3a";

    // A FIXED instant so dueness is deterministic (never the wall clock). The app timezone defaults to
    // America/Chicago; in June that is CDT (UTC-5), so this UTC instant is local Sunday 2026-06-07 18:30 —
    // Sunday, hour 18 ≥ the seeded SendHourLocal (18) → both households are DUE at this instant. The Mon–Sun
    // equity week containing it starts local Monday 2026-06-01, so the seeded mid-week completions are in-window.
    public static readonly DateTime FixedNowUtc = new(2026, 6, 7, 23, 30, 0, DateTimeKind.Utc);

    // Distinct webhook URLs per household — the ONLY thing the IDigestSender boundary sees that identifies a
    // household (it never receives a HouseholdId). Tests distinguish sends/failures by these URLs.
    public const string WebhookUrlA = "https://discord.com/api/webhooks/1000000000000000001/HOUSEHOLD-A-SECRET-AAAA";
    public const string WebhookUrlB = "https://discord.com/api/webhooks/1000000000000000002/HOUSEHOLD-B-SECRET-BBBB";

    /// <summary>
    /// The capturing fake bound in place of <see cref="DiscordWebhookDigestSender"/> — records every send
    /// (no real network call) and can be told to throw for one household's URL (failure-isolation test).
    /// A test may configure <see cref="FakeDigestSender.ThrowForUrl"/> BEFORE <see cref="EnsureSeededAsync"/>.
    /// </summary>
    public FakeDigestSender DigestSender { get; } = new();

    /// <summary>The fixed clock injected in place of <c>TimeProvider.System</c> so "now" is deterministic.</summary>
    public FixedTimeProvider Clock { get; } = new(FixedNowUtc);

    /// <summary>
    /// The trigger token configured for the host. The default factory configures the known
    /// <see cref="DigestTriggerToken"/>; <see cref="UnconfiguredDigestWebAppFactory"/> overrides this to ""
    /// so the run endpoint takes the refuse-if-unconfigured (503) path.
    /// </summary>
    protected virtual string ConfiguredTriggerToken => DigestTriggerToken;

    private bool _seeded;
    private readonly SemaphoreSlim _seedLock = new(1, 1);

    // This factory boots against its OWN uniquely-named database on the shared container (created in
    // EnsureSeededAsync before the host is built), so its real-host MigrateAsync path does not collide with the
    // EnsureCreated-based service-level test sharing the same container. Falls back to the default DB if a test
    // accesses the host without seeding first.
    private string? _connectionString;

    /// <summary>
    /// This factory's per-class database connection string (assigned in <see cref="EnsureSeededAsync"/>). Lets
    /// a test build a <see cref="PostgresDbContextFactory"/> for raw-row assertions (e.g. that
    /// <c>WebhookUrlProtected</c> is ciphertext, or that a failed household's <c>LastSentAt</c> was compensated).
    /// </summary>
    public string ConnectionString =>
        _connectionString ?? throw new InvalidOperationException("Call EnsureSeededAsync() before reading ConnectionString.");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.UseSetting("ConnectionStrings:DefaultConnection", _connectionString ?? postgres.ConnectionString);
        // Satisfy the mandatory Google OAuth config keys so Program.cs does not throw at startup (council C3).
        builder.UseSetting("Authentication:Google:ClientId", "test-client-id");
        builder.UseSetting("Authentication:Google:ClientSecret", "test-client-secret");

        // The digest-run endpoint refuses (503) unless this is configured; tests assert valid/missing/wrong
        // token against this known value. The unconfigured-token (503) variant is exercised by
        // UnconfiguredDigestWebAppFactory, which overrides ConfiguredTriggerToken to "" (treated as unset).
        builder.UseSetting("CHORES_DIGEST_TRIGGER_TOKEN", ConfiguredTriggerToken);

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

            // Bind the capturing fake in place of the real Discord sender so NO real network call ever occurs
            // (WP-08 failure criterion). Last-registration-wins over Program.cs's
            // AddScoped<IDigestSender, DiscordWebhookDigestSender>() — registering the singleton instance also
            // lets tests read the recorded invocations + inject a per-URL failure.
            services.AddSingleton<IDigestSender>(DigestSender);

            // Freeze the clock: replace AddSingleton(TimeProvider.System) so DigestService/DigestDue evaluate
            // dueness against FixedNowUtc, not the wall clock (determinism — WP-08 non-flaky requirement). The
            // settings service also reads this for CreatedAt/UpdatedAt.
            services.AddSingleton<TimeProvider>(Clock);
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

            // Provision this factory's own database on the shared container BEFORE the host is built (the
            // Services access below triggers ConfigureWebHost, which reads _connectionString).
            _connectionString ??= await postgres.CreateDatabaseConnectionStringAsync();

            var dbFactory = Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            await using var context = await dbFactory.CreateDbContextAsync();

            // Apply migrations on the real Postgres. If the WP-01 migration is non-additive or breaks on PG,
            // this throws and the suite fails loudly (a real-bug signal, not something to patch here).
            await context.Database.MigrateAsync();

            // The digest-settings rows must be encrypted with the SAME DataProtection keys the running host
            // uses (so DigestService.GetDecryptedWebhookAsync round-trips at run time). Resolve the real
            // settings service from the booted host and seed the webhooks through it inside the guarded block.
            var settingsService = Services.GetRequiredService<IDigestSettingsService>();
            await SeedAsync(context, settingsService);
            _seeded = true;
        }
        finally
        {
            _seedLock.Release();
        }
    }

    private static async Task SeedAsync(ApplicationDbContext context, IDigestSettingsService settingsService)
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

        // ── v1.1 (WP-08): multi-member completions per household, in the equity week of FixedNowUtc ──────
        // FixedNowUtc is local Sunday 2026-06-07; the Mon–Sun week starts local Monday 2026-06-01, so a
        // mid-week 2026-06-03 12:00Z completion is inside the week window for BOTH the equity endpoint and the
        // digest model. DISTINCT per household so the multi-tenant-isolation assertion is non-trivial:
        //   Household A: Alice 2×Standard (4 pts) + Amy 1×Quick (1 pt) = 3 completions, 5 pts, 2 contributors.
        //   Household B: Bob 1×BigJob (3 pts)                          = 1 completion, 3 pts, 1 contributor.
        // CompletionId is ValueGeneratedOnAdd — do NOT set it (let PG assign).
        var inWeek = new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc);

        context.ChoreCompletions.AddRange(
            new ChoreCompletion
            {
                HouseholdId = HouseholdAId,
                ChoreId = PileChoreAId,
                CompletedByUserId = UserAId,
                CompletedAt = inWeek,
                EffortPointsSnapshot = ChoreEffort.PointsFor(EffortTier.Standard)
            },
            new ChoreCompletion
            {
                HouseholdId = HouseholdAId,
                ChoreId = PileChoreAId,
                CompletedByUserId = UserAId,
                CompletedAt = inWeek.AddHours(1),
                EffortPointsSnapshot = ChoreEffort.PointsFor(EffortTier.Standard)
            },
            new ChoreCompletion
            {
                HouseholdId = HouseholdAId,
                ChoreId = PileChoreAId,
                CompletedByUserId = UserA2Id,
                CompletedAt = inWeek.AddHours(2),
                EffortPointsSnapshot = ChoreEffort.PointsFor(EffortTier.Quick)
            },
            new ChoreCompletion
            {
                HouseholdId = HouseholdBId,
                ChoreId = ChoreBId,
                CompletedByUserId = UserBId,
                CompletedAt = inWeek,
                EffortPointsSnapshot = ChoreEffort.PointsFor(EffortTier.BigJob)
            });

        await context.SaveChangesAsync();

        // The Households/Users above were inserted with EXPLICIT identity ids (1,2 / 1,2,3) for deterministic
        // fixtures, which does NOT advance PostgreSQL's identity sequence. Any test that then creates a
        // Household/User through the app (e.g. the settings add-member endpoint, or cluster-C's approve flow)
        // would get a generated id colliding with a seeded one → PK violation. Resync both sequences to MAX(id)
        // so generated ids continue past the seed. Harmless for tests that never create these rows.
        await context.Database.ExecuteSqlRawAsync(
            "SELECT setval(pg_get_serial_sequence('\"Users\"', 'Id'), (SELECT MAX(\"Id\") FROM \"Users\"))");
        await context.Database.ExecuteSqlRawAsync(
            "SELECT setval(pg_get_serial_sequence('\"Households\"', 'Id'), (SELECT MAX(\"Id\") FROM \"Households\"))");

        // ── v1.1 (WP-08): both households' digest settings — enabled, due at FixedNowUtc, distinct webhooks.
        // Seeded THROUGH the real settings service so the webhook ciphertext is encrypted with the host's
        // DataProtection keys (DigestService.GetDecryptedWebhookAsync must round-trip it at send time).
        // SendDayOfWeek=Sunday + SendHourLocal=18 + LastSentAt=null ⇒ DUE at FixedNowUtc (local Sun 18:30).
        await settingsService.UpdateAsync(HouseholdAId, new DigestSettingsUpdate(
            Enabled: true, Cadence: DigestCadence.Weekly,
            SendDayOfWeek: DayOfWeek.Sunday, SendHourLocal: 18,
            WebhookProvided: true, WebhookUrl: WebhookUrlA));

        await settingsService.UpdateAsync(HouseholdBId, new DigestSettingsUpdate(
            Enabled: true, Cadence: DigestCadence.Weekly,
            SendDayOfWeek: DayOfWeek.Sunday, SendHourLocal: 18,
            WebhookProvided: true, WebhookUrl: WebhookUrlB));
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
/// A <see cref="ChoresWebAppFactory"/> variant whose digest trigger token is UNCONFIGURED (empty), so the
/// <c>POST /api/chores/digest/run</c> endpoint takes the refuse-if-unconfigured path and returns <b>503</b>
/// with a non-empty JSON body. Everything else (seed, fake sender, fixed clock) is identical to the base.
/// </summary>
public sealed class UnconfiguredDigestWebAppFactory(PostgresContainerFixture postgres) : ChoresWebAppFactory(postgres)
{
    // Empty ⇒ ValidateTriggerToken sees string.IsNullOrEmpty(configuredToken) ⇒ 503 (feature off).
    protected override string ConfiguredTriggerToken => string.Empty;
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
