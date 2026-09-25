using System.Security.Claims;
using System.Text.Json;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Tenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FamilyCoordinationApp.Tests.Tenancy;

/// <summary>
/// <see cref="CallerTenantMiddleware"/> invoked directly (fca-household-scope WP-01 item 11, the A5 401 proof).
/// An unresolvable-but-authorized principal can't be reached through HTTP in steady state: the whitelist handler
/// and the resolver both key on <c>Users.Email</c>, and before setup the first-run middleware redirects /api to
/// /setup ahead of authorization. So the 401 contract (constraints M5) is proven here, against the real
/// <see cref="TenantDbContextFactory"/> over an InMemory database.
/// </summary>
public sealed class CallerTenantMiddlewareTests
{
    private const string KnownEmail = "alice@household-a.test";

    private sealed class Harness
    {
        public required DefaultHttpContext Http { get; init; }
        public required TenantContext Tenant { get; init; }
        public required IDbContextFactory<ApplicationDbContext> DbFactory { get; init; }
        public bool NextCalled { get; set; }
    }

    private static Harness Arrange(Endpoint? endpoint, string? email)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using (var seed = new ApplicationDbContext(options))
        {
            seed.Households.Add(new Household { Id = 1, Name = "A", CreatedAt = DateTime.UtcNow });
            seed.Users.Add(new User
            {
                Id = 7,
                HouseholdId = 1,
                Email = KnownEmail,
                DisplayName = "Alice",
                Initials = "A",
                IsWhitelisted = true,
                CreatedAt = DateTime.UtcNow,
            });
            seed.SaveChanges();
        }

        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        http.Response.Body = new MemoryStream();
        if (email is not null)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Email, email)], "Test"));
        }
        http.SetEndpoint(endpoint);

        var tenancyOptions = Options.Create(new TenancyOptions());
        var tenant = new TenantContext(new HttpContextAccessor { HttpContext = http }, tenancyOptions);
        var dbFactory = new TenantDbContextFactory(options, tenant, tenancyOptions, TimeProvider.System);
        return new Harness { Http = http, Tenant = tenant, DbFactory = dbFactory };
    }

    private static Endpoint Marked() =>
        new(_ => Task.CompletedTask, new EndpointMetadataCollection(TenantScopedMetadata.Instance), "marked");

    private static Endpoint Unmarked() =>
        new(_ => Task.CompletedTask, new EndpointMetadataCollection(), "unmarked");

    private static Task InvokeAsync(Harness h)
    {
        var middleware = new CallerTenantMiddleware(_ =>
        {
            h.NextCalled = true;
            return Task.CompletedTask;
        });
        return middleware.InvokeAsync(h.Http, h.Tenant, h.DbFactory);
    }

    private static async Task<string?> ReadMessageAsync(HttpResponse response)
    {
        response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(response.Body);
        return doc.RootElement.GetProperty("message").GetString();
    }

    [Fact]
    public async Task Marked_endpoint_with_an_authenticated_principal_that_has_no_Users_row_answers_401_JSON_and_stops()
    {
        var h = Arrange(Marked(), email: "nobody@nowhere.test");

        await InvokeAsync(h);

        h.Http.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized, "M5: never 400 or 403");
        h.Http.Response.ContentType.Should().StartWith("application/json");
        (await ReadMessageAsync(h.Http.Response)).Should().NotBeNullOrWhiteSpace();
        h.NextCalled.Should().BeFalse("an unresolvable caller must not reach the handler");
        h.Tenant.State.Should().Be(TenantState.Unset);
    }

    [Fact]
    public async Task Marked_endpoint_with_no_email_claim_answers_401_JSON_and_stops()
    {
        var h = Arrange(Marked(), email: null);

        await InvokeAsync(h);

        h.Http.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        h.Http.Response.ContentType.Should().StartWith("application/json");
        (await ReadMessageAsync(h.Http.Response)).Should().NotBeNullOrWhiteSpace();
        h.NextCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Marked_endpoint_with_a_resolvable_caller_sets_Caller_and_continues()
    {
        var h = Arrange(Marked(), KnownEmail);

        await InvokeAsync(h);

        h.NextCalled.Should().BeTrue();
        h.Tenant.State.Should().Be(TenantState.Caller);
        h.Tenant.HouseholdId.Should().Be(1);
        h.Tenant.UserId.Should().Be(7);
        h.Http.Response.StatusCode.Should().Be(StatusCodes.Status200OK, "the middleware wrote nothing");
    }

    [Fact]
    public async Task Unmarked_endpoint_passes_through_with_the_tenant_still_Unset()
    {
        // Resolvable caller on purpose: an unmarked endpoint must not be resolved even when it could be.
        var h = Arrange(Unmarked(), KnownEmail);

        await InvokeAsync(h);

        h.NextCalled.Should().BeTrue();
        h.Tenant.State.Should().Be(TenantState.Unset);
        h.Http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task No_endpoint_passes_through_with_the_tenant_still_Unset()
    {
        var h = Arrange(endpoint: null, email: null);

        await InvokeAsync(h);

        h.NextCalled.Should().BeTrue();
        h.Tenant.State.Should().Be(TenantState.Unset);
    }

    [Fact]
    public async Task A_context_from_the_factory_carries_the_tenant_the_middleware_set()
    {
        var h = Arrange(Marked(), KnownEmail);
        await InvokeAsync(h);

        await using var db = await h.DbFactory.CreateDbContextAsync();

        db.Tenant.Should().BeSameAs(h.Tenant);
        db.Tenant.State.Should().Be(TenantState.Caller);
        db.Tenant.HouseholdId.Should().Be(1);
    }

    // ── CallerScope binding (item 8): no handler uses it until WP-04, so the binder is proven here ──

    [Fact]
    public async Task CallerScope_binds_from_a_Caller_tenant()
    {
        var h = Arrange(Marked(), KnownEmail);
        await InvokeAsync(h);
        h.Http.RequestServices = new ServiceCollection().AddSingleton<ITenantContext>(h.Tenant).BuildServiceProvider();

        var scope = await CallerScope.BindAsync(h.Http);

        scope.Should().Be(new CallerScope(1, 7));
    }

    [Fact]
    public async Task CallerScope_throws_when_the_tenant_is_not_Caller()
    {
        // A forgotten RequireTenant(): the tenant is still Unset when the handler binds. Must be loud.
        var h = Arrange(Unmarked(), KnownEmail);
        await InvokeAsync(h);
        h.Http.RequestServices = new ServiceCollection().AddSingleton<ITenantContext>(h.Tenant).BuildServiceProvider();

        var act = async () => await CallerScope.BindAsync(h.Http);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
