using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Tenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// The tenancy plumbing through the real host (fca-household-scope WP-01; verification V1/V2). WP-01 enforces
/// nothing, so these tests OBSERVE the tenant instead of relying on a filter: a recording
/// <see cref="ITenantContext"/> decorator (<see cref="ChoresWebAppFactory.TenantContextDecorator"/>, via
/// <c>ConfigureTestServices</c>; no test-only endpoints, M12) captures each request's tenant instance, and the
/// test reads the state the caller-resolution middleware left on it.
/// <para>Also here: the DI shape of the scoped factory, endpoint-coverage fact 6, fact 7 (the A1 guard) and the
/// test-host source scan, each with an in-tree negative control.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class TenantPlumbingTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ConcurrentQueue<RecordingTenantContext> _recorded = new();
    private ChoresWebAppFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new ChoresWebAppFactory(postgres)
        {
            TenantContextDecorator = (inner, sp) =>
            {
                var recording = new RecordingTenantContext(
                    inner, sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.Request.Path.Value);
                _recorded.Enqueue(recording);
                return recording;
            },
        };
        await _factory.EnsureSeededAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    // ── V1: the tenant is resolved per request and reaches the request's scope ─────────────────────

    /// <summary>One GET per marked route group (D3's marker map). Status is irrelevant: the middleware runs first.</summary>
    public static readonly string[] MarkedGroupProbes =
    [
        "/api/me",
        "/api/presence/users",
        "/api/shopping-lists/",
        "/api/chores/board",
        "/api/rooms/",
        "/api/meal-plan/board",
        "/api/meal-plan/calendar-token",
        "/api/recipes/",
        "/api/dashboard/",
        "/api/settings/categories/",
        "/api/settings/connections/",
        "/api/settings/feedback/",
        "/uploads/1/no-such-file.png",
    ];

    [Fact]
    public async Task V1_Alices_request_to_every_marked_group_records_Caller_1_1()
    {
        using var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);
        var failures = new List<string>();

        foreach (var path in MarkedGroupProbes)
        {
            using var response = await client.GetAsync(path);
            var records = _recorded.Where(r => r.RequestPath == path).ToList();
            if (records.Count == 0)
            {
                failures.Add($"{path} ({(int)response.StatusCode}): no tenant was resolved in the request's scope");
                continue;
            }
            foreach (var r in records)
            {
                var state = (r.State, r.HouseholdId, r.UserId);
                if (state != (TenantState.Caller, ChoresWebAppFactory.HouseholdAId, ChoresWebAppFactory.UserAId))
                    failures.Add($"{path} ({(int)response.StatusCode}): tenant was {state}, expected Caller(1, 1)");
            }
        }

        failures.Should().BeEmpty(
            "the middleware must set Caller(household, user) for every RequireTenant() route; a group missing its " +
            "marker leaves the request's tenant Unset:\n" + string.Join("\n", failures));
    }

    [Fact]
    public async Task V1_the_anonymous_calendar_feed_records_Unset()
    {
        using var client = _factory.CreateAnonymousClient();
        const string feed = "/api/calendar/meal-plan.ics";

        using var _ = await client.GetAsync($"{feed}?token=not-a-real-token");

        var records = _recorded.Where(r => r.RequestPath == feed).ToList();
        records.Should().NotBeEmpty("the middleware resolves the request's tenant even on an unmarked route");
        records.Should().OnlyContain(r => r.State == TenantState.Unset,
            "the feed is unmarked (D3): its tenant comes from its token via RunAs (WP-02), never from a caller");
    }

    [Fact]
    public async Task V1_an_unmarked_request_by_a_resolvable_caller_is_still_Unset()
    {
        // Alice is resolvable, but the feed is unmarked: the middleware must not resolve her there.
        using var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);
        const string feed = "/api/calendar/meal-plan.ics";

        using var _ = await client.GetAsync($"{feed}?token=not-a-real-token");

        _recorded.Where(r => r.RequestPath == feed).Should().NotBeEmpty()
            .And.OnlyContain(r => r.State == TenantState.Unset);
    }

    // ── The scoped factory (WP-01 item 3) ──────────────────────────────────────────────────────────

    [Fact]
    public void Exactly_one_factory_descriptor_is_registered_and_it_is_the_scoped_TenantDbContextFactory()
    {
        var descriptors = CaptureDescriptors();

        var factories = descriptors.Where(d => d.ServiceType == typeof(IDbContextFactory<ApplicationDbContext>)).ToList();
        factories.Should().ContainSingle("a second factory registration could hand out tenant-less contexts");
        factories[0].Lifetime.Should().Be(ServiceLifetime.Scoped);
        factories[0].ImplementationType.Should().Be(typeof(TenantDbContextFactory));

        var tenants = descriptors.Where(d => d.ServiceType == typeof(ITenantContext)).ToList();
        tenants.Should().ContainSingle();
        tenants[0].Lifetime.Should().Be(ServiceLifetime.Scoped, "the tenant is per request (D2), never shared");

        // ApplicationDbContext stays registered for EF design-time, re-registered to resolve THROUGH the factory.
        var contexts = descriptors.Where(d => d.ServiceType == typeof(ApplicationDbContext)).ToList();
        contexts.Should().ContainSingle();
        contexts[0].Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public async Task A_context_from_a_scopes_factory_carries_that_scopes_tenant()
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        dbFactory.Should().BeOfType<TenantDbContextFactory>();
        await using var asyncContext = await dbFactory.CreateDbContextAsync();
        await using var syncContext = dbFactory.CreateDbContext();
        asyncContext.Tenant.Should().BeSameAs(tenant);
        syncContext.Tenant.Should().BeSameAs(tenant);

        using var otherScope = _factory.Services.CreateScope();
        await using var otherContext = await otherScope.ServiceProvider
            .GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync();
        otherContext.Tenant.Should().NotBeSameAs(tenant, "each DI scope has its own tenant");
    }

    [Fact]
    public async Task Resolving_ApplicationDbContext_directly_goes_through_the_wrapper()
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        context.Tenant.Should().BeSameAs(tenant, "no resolution path may bypass TenantDbContextFactory");
        (await context.Households.AnyAsync()).Should().BeTrue("the resolved context is a working context");
    }

    [Fact]
    public void The_tenancy_options_bind_from_configuration_and_the_test_host_sets_only_OutOfRequest()
    {
        using var scope = _factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<TenancyOptions>>().Value;

        options.OutOfRequest.Should().Be(OutOfRequestMode.Unfiltered, "TestHostTenancy.Apply sets it (D15)");
        options.EnforceFilter.Should().BeTrue("M7: nothing turns the kill switch off");
        options.EnforceWrites.Should().BeTrue("M7: nothing turns the kill switch off");
    }

    // ── Fact 6: endpoint coverage ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The pinned unmarked list (D3): the only /api or /uploads endpoints without <c>RequireTenant()</c>. The digest
    /// cron and the anonymous feed carry no caller, and the site-admin groups are global.
    /// </summary>
    internal static readonly string[] PinnedUnmarked =
    [
        "/api/chores/digest/run",
        "/api/calendar/meal-plan.ics",
        "/api/settings/household-requests/",
        "/api/settings/household-requests/{id:int}/approve",
        "/api/settings/household-requests/{id:int}/reject",
        "/api/settings/households/",
    ];

    internal static bool IsTenantSurface(string route) =>
        route.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
        || route.StartsWith("/uploads", StringComparison.OrdinalIgnoreCase);

    internal static List<string> EndpointCoverageViolations(IEnumerable<(string Route, bool Marked)> endpoints) =>
        endpoints
            .Where(e => IsTenantSurface(e.Route) && !e.Marked && !PinnedUnmarked.Contains(e.Route))
            .Select(e => e.Route)
            .ToList();

    private List<(string Route, bool Marked)> RealEndpoints() =>
        _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => (e.RoutePattern.RawText ?? string.Empty, e.Metadata.GetMetadata<TenantScopedMetadata>() is not null))
            .ToList();

    [Fact]
    public void Fact6_every_api_and_uploads_endpoint_is_marked_or_on_the_pinned_unmarked_list()
    {
        var endpoints = RealEndpoints();

        var violations = EndpointCoverageViolations(endpoints);

        violations.Should().BeEmpty(
            "every /api and /uploads endpoint must carry RequireTenant() (TenantScopedMetadata), or be on the " +
            "reviewed unmarked list. Unmarked:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void Fact6_the_pinned_unmarked_list_is_exactly_the_real_unmarked_set()
    {
        // The pin must not go stale: an entry that got marked, renamed or deleted is an edit to review.
        var unmarked = RealEndpoints().Where(e => IsTenantSurface(e.Route) && !e.Marked).Select(e => e.Route);

        unmarked.Should().BeEquivalentTo(PinnedUnmarked);
    }

    [Fact]
    public void Fact6_guard_the_guard_the_endpoint_population_is_substantial()
    {
        // An empty data source would pass the coverage fact while checking nothing.
        var marked = RealEndpoints().Count(e => IsTenantSurface(e.Route) && e.Marked);
        // 108 at WP-01: 114 Map* calls in Endpoints/*.cs minus the 6 pinned unmarked (measured 2026-09-25).
        marked.Should().BeGreaterThan(80, "a collapse far below the measured 108 means the data source stopped being seen");
    }

    [Fact]
    public void NC_fact6_an_unmarked_api_endpoint_off_the_pin_is_flagged()
    {
        var synthetic = new[]
        {
            ("/api/rooms/", true),
            ("/api/chores/digest/run", false),
            ("/api/rogue/unmarked", false),
            ("/uploads/{householdId:int}/{fileName}", false),
            ("/account/logout", false),
        };

        EndpointCoverageViolations(synthetic).Should()
            .BeEquivalentTo(["/api/rogue/unmarked", "/uploads/{householdId:int}/{fileName}"]);
    }

    // ── Fact 7: the A1 guard (no singleton or hosted service holds tenant state) ────────────────────

    private static readonly Type[] ScopedTenantTypes =
    [
        typeof(IDbContextFactory<ApplicationDbContext>),
        typeof(ITenantContext),
        typeof(ApplicationDbContext),
    ];

    /// <summary>
    /// Singletons and hosted services whose implementation type's constructor takes the factory, the tenant or a
    /// context. Such a service would capture one scope's tenant for the life of the process (A1).
    /// <para><b>Stated limit:</b> factory-lambda and instance registrations have no inspectable implementation type,
    /// so they are invisible to this fact. Today's lambda/instance singletons (TimeProvider, TimeZoneInfo, the test
    /// fakes) take no constructor dependencies at all.</para>
    /// </summary>
    internal static List<string> SingletonTenantDependents(IEnumerable<ServiceDescriptor> descriptors)
    {
        var offenders = new List<string>();
        foreach (var d in descriptors)
        {
            if (d.Lifetime != ServiceLifetime.Singleton && d.ServiceType != typeof(IHostedService)) continue;
            var implementation = d.IsKeyedService ? d.KeyedImplementationType : d.ImplementationType;
            if (implementation is null) continue;

            var takesTenantState = implementation
                .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .SelectMany(c => c.GetParameters())
                .Any(p => ScopedTenantTypes.Contains(p.ParameterType));
            if (takesTenantState) offenders.Add($"{d.ServiceType.Name} → {implementation.FullName} ({d.Lifetime})");
        }
        return offenders;
    }

    [Fact]
    public void Fact7_no_singleton_or_hosted_service_takes_the_factory_the_tenant_or_a_context()
    {
        var descriptors = CaptureDescriptors();
        descriptors.Count(d => d.Lifetime == ServiceLifetime.Singleton).Should()
            .BeGreaterThan(20, "guard the guard: the captured collection is the real host's");

        SingletonTenantDependents(descriptors).Should().BeEmpty(
            "a singleton or hosted service built from the root provider would pin one scope's tenant for the process");
    }

    [Fact]
    public void NC_fact7_a_singleton_or_hosted_service_taking_tenant_state_is_flagged()
    {
        var synthetic = new ServiceCollection();
        synthetic.AddSingleton<SingletonTakingFactory>();
        synthetic.AddSingleton<IHostedService, HostedTakingTenant>();
        synthetic.AddScoped<ScopedTakingContext>(); // scoped is fine
        synthetic.AddSingleton<SingletonTakingNothing>();
        synthetic.AddKeyedSingleton<SingletonTakingFactory>("keyed");

        SingletonTenantDependents(synthetic).Should().HaveCount(3)
            .And.Contain(o => o.Contains(nameof(SingletonTakingFactory)))
            .And.Contain(o => o.Contains(nameof(HostedTakingTenant)));
    }

    private sealed class SingletonTakingFactory(IDbContextFactory<ApplicationDbContext> factory)
    {
        public object Factory => factory;
    }

    private sealed class HostedTakingTenant(ITenantContext tenant) : IHostedService
    {
        public object Tenant => tenant;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ScopedTakingContext(ApplicationDbContext context)
    {
        public object Context => context;
    }

    private sealed class SingletonTakingNothing;

    /// <summary>The real host's service collection after every registration, test overrides included.</summary>
    private List<ServiceDescriptor> CaptureDescriptors()
    {
        List<ServiceDescriptor>? captured = null;
        using var capturing = _factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(services => captured = [.. services]));
        _ = capturing.Services; // builds the host, running every ConfigureServices callback
        captured.Should().NotBeNull();
        return captured!;
    }

    // ── Every root test host applies TestHostTenancy (D15) ─────────────────────────────────────────

    internal static string TestProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FamilyCoordinationApp.Tests.csproj")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test must run from within the test project's tree");
        return dir!.FullName;
    }

    /// <summary>
    /// The named host classes whose declaration body lacks a <c>TestHostTenancy.Apply(</c> call, or whose
    /// declaration can't be found in the given sources at all. The body is found by brace matching from the
    /// class declaration (lexically naive; a brace inside a string literal would skew it).
    /// </summary>
    internal static List<string> HostsMissingTenancyApply(IEnumerable<string> hostNames, IReadOnlyCollection<string> sources)
    {
        var missing = new List<string>();
        foreach (var name in hostNames)
        {
            var body = sources
                .Select(s => (Source: s, Match: Regex.Match(s, $@"\bclass\s+{Regex.Escape(name)}\b")))
                .Where(x => x.Match.Success)
                .Select(x => ClassBody(x.Source, x.Match.Index))
                .FirstOrDefault();
            if (body is null) missing.Add($"{name} (declaration not found in the test sources)");
            else if (!body.Contains("TestHostTenancy.Apply(", StringComparison.Ordinal)) missing.Add(name);
        }
        return missing;
    }

    private static string? ClassBody(string source, int declarationIndex)
    {
        var open = source.IndexOf('{', declarationIndex);
        if (open < 0) return null;
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        return null;
    }

    [Fact]
    public void Every_root_WebApplicationFactory_host_calls_TestHostTenancy_Apply()
    {
        // Reflection finds the hosts (it cannot miss one); the source scan checks each one's body.
        var hosts = typeof(TenantPlumbingTests).Assembly.GetTypes()
            .Where(t => t.BaseType == typeof(WebApplicationFactory<Program>))
            .Select(t => t.Name)
            .ToList();
        hosts.Should().Contain([nameof(ChoresWebAppFactory), nameof(DevAuthTestingWebAppFactory)],
            "guard the guard: reflection sees the two known root hosts");

        var sources = Directory.EnumerateFiles(TestProjectRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText)
            .ToList();

        HostsMissingTenancyApply(hosts, sources).Should().BeEmpty(
            "every root WebApplicationFactory<Program> must call TestHostTenancy.Apply(builder) in ConfigureWebHost, " +
            "or its root-provider test setup throws once WP-02 turns the filter on (D15)");
    }

    [Fact]
    public void NC_a_root_host_without_TestHostTenancy_Apply_is_flagged()
    {
        const string compliant = """
            public sealed class GoodHost : WebApplicationFactory<Program>
            {
                protected override void ConfigureWebHost(IWebHostBuilder builder)
                {
                    builder.UseEnvironment("Testing");
                    TestHostTenancy.Apply(builder);
                }
            }
            """;
        const string rogue = """
            public sealed class RogueHost : WebApplicationFactory<Program>
            {
                protected override void ConfigureWebHost(IWebHostBuilder builder)
                {
                    builder.UseEnvironment("Testing");
                }
            }
            // A mention outside the class body must not count: TestHostTenancy.Apply(builder)
            """;

        HostsMissingTenancyApply(["GoodHost", "RogueHost", "GhostHost"], [compliant, rogue]).Should()
            .BeEquivalentTo(["RogueHost", "GhostHost (declaration not found in the test sources)"]);
    }
}

/// <summary>
/// The V1 recording decorator: delegates every member to a real <see cref="TenantContext"/> and remembers which
/// request path its DI scope served, so a test can read the state the middleware left on it. Test-only; registered
/// through <see cref="ChoresWebAppFactory.TenantContextDecorator"/>.
/// </summary>
internal sealed class RecordingTenantContext(ITenantContext inner, string? requestPath) : ITenantContext
{
    public string? RequestPath { get; } = requestPath;

    public TenantState State => inner.State;
    public int? HouseholdId => inner.HouseholdId;
    public int? UserId => inner.UserId;
    public bool IsUnfiltered => inner.IsUnfiltered;
    public bool IsOutOfRequest => inner.IsOutOfRequest;
    public OutOfRequestMode OutOfRequestMode => inner.OutOfRequestMode;
    public bool IsCrossTenantWriteAllowed => inner.IsCrossTenantWriteAllowed;
    public void SetCaller(int householdId, int userId) => inner.SetCaller(householdId, userId);
    public IDisposable RunAs(int householdId) => inner.RunAs(householdId);
    public IDisposable AllowCrossTenantWrite(string reason) => inner.AllowCrossTenantWrite(reason);
}
