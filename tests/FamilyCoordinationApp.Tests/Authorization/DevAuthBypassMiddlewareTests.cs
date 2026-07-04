using System.Security.Claims;
using FamilyCoordinationApp.Authorization;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FamilyCoordinationApp.Tests.Authorization;

/// <summary>
/// WP-03 security unit tests for <see cref="DevAuthBypassMiddleware"/> (de-Blazor). The load-bearing property
/// (MN3 / E1): the bypass is INERT outside Development, and inside Development it injects a real seed identity's
/// claims (Email/Name/NameIdentifier) — never a fabricated phantom. Docker-free (the middleware is invoked
/// directly, no host boot), so this runs in the fast suite and targets the env gate precisely.
/// </summary>
public sealed class DevAuthBypassMiddlewareTests
{
    private static IWebHostEnvironment Env(string name)
    {
        var mock = new Mock<IWebHostEnvironment>();
        mock.SetupGet(e => e.EnvironmentName).Returns(name);
        return mock.Object;
    }

    private static IConfiguration Config(Dictionary<string, string?>? values = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(values ?? new Dictionary<string, string?>()).Build();

    /// <summary>A db factory that throws if touched — proves a code path never queries the database.</summary>
    private static IDbContextFactory<ApplicationDbContext> ThrowingDbFactory()
    {
        var mock = new Mock<IDbContextFactory<ApplicationDbContext>>();
        mock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the database must not be touched on this path"));
        return mock.Object;
    }

    /// <summary>An InMemory-backed db factory, optionally seeded with a single user (the dev-identity source).</summary>
    private static IDbContextFactory<ApplicationDbContext> InMemoryDbFactory((string email, string name)? seedUser)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using (var ctx = new ApplicationDbContext(options))
        {
            if (seedUser is { } u)
            {
                ctx.Households.Add(new Household { Id = 1, Name = "H", CreatedAt = DateTime.UtcNow });
                ctx.Users.Add(new User
                {
                    Id = 1,
                    HouseholdId = 1,
                    Email = u.email,
                    DisplayName = u.name,
                    Initials = "H",
                    IsWhitelisted = true,
                    CreatedAt = DateTime.UtcNow
                });
                ctx.SaveChanges();
            }
        }

        var mock = new Mock<IDbContextFactory<ApplicationDbContext>>();
        mock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));
        return mock.Object;
    }

    private static (DevAuthBypassMiddleware mw, Func<bool> nextCalled) Middleware()
    {
        var called = false;
        var mw = new DevAuthBypassMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            NullLogger<DevAuthBypassMiddleware>.Instance);
        return (mw, () => called);
    }

    [Fact]
    public async Task Production_AnonymousRequest_DoesNotAuthenticate_AndPassesThrough()
    {
        var (mw, nextCalled) = Middleware();
        var ctx = new DefaultHttpContext();

        await mw.InvokeAsync(ctx, Env("Production"),
            Config(new() { ["DevAuth:Email"] = "should-be-ignored@test" }), ThrowingDbFactory());

        ctx.User.Identity!.IsAuthenticated.Should().BeFalse("the bypass must be inert outside Development");
        nextCalled().Should().BeTrue();
    }

    [Fact]
    public async Task NonDevelopmentEnv_Testing_IsAlsoInert()
    {
        // MN3 says "any non-Development env"; Testing is the CI / integration environment — prove it is inert too.
        var (mw, nextCalled) = Middleware();
        var ctx = new DefaultHttpContext();

        await mw.InvokeAsync(ctx, Env("Testing"),
            Config(new() { ["DevAuth:Email"] = "should-be-ignored@test" }), ThrowingDbFactory());

        ctx.User.Identity!.IsAuthenticated.Should().BeFalse();
        nextCalled().Should().BeTrue();
    }

    [Fact]
    public async Task Development_WithConfigIdentity_InjectsAllClaims_WithoutTouchingDb()
    {
        var (mw, nextCalled) = Middleware();
        var ctx = new DefaultHttpContext();

        await mw.InvokeAsync(ctx, Env("Development"), Config(new()
        {
            ["DevAuth:Email"] = "dev@example.test",
            ["DevAuth:Name"] = "Dev User",
            ["DevAuth:GoogleId"] = "google-abc-123",
        }), ThrowingDbFactory());

        ctx.User.Identity!.IsAuthenticated.Should().BeTrue();
        ctx.User.FindFirst(ClaimTypes.Email)!.Value.Should().Be("dev@example.test");
        ctx.User.FindFirst(ClaimTypes.Name)!.Value.Should().Be("Dev User");
        ctx.User.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be("google-abc-123");
        nextCalled().Should().BeTrue();
    }

    [Fact]
    public async Task Development_WithRealAuthCookie_DoesNotOverride()
    {
        var (mw, nextCalled) = Middleware();
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Cookie"] = "FamilyApp.Auth=a-real-session-token";

        await mw.InvokeAsync(ctx, Env("Development"),
            Config(new() { ["DevAuth:Email"] = "dev@example.test" }), ThrowingDbFactory());

        ctx.User.Identity!.IsAuthenticated.Should().BeFalse("a real cookie must be handled by the real cookie handler");
        nextCalled().Should().BeTrue();
    }

    [Fact]
    public async Task Development_AlreadyAuthenticated_DoesNotOverrideRealPrincipal()
    {
        var (mw, nextCalled) = Middleware();
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Email, "real@user.test") }, "Cookies"))
        };

        await mw.InvokeAsync(ctx, Env("Development"),
            Config(new() { ["DevAuth:Email"] = "dev@example.test" }), ThrowingDbFactory());

        ctx.User.FindFirst(ClaimTypes.Email)!.Value.Should().Be("real@user.test");
        nextCalled().Should().BeTrue();
    }

    [Fact]
    public async Task Development_NoConfig_UsesFirstDbUser()
    {
        var (mw, nextCalled) = Middleware();
        var ctx = new DefaultHttpContext();

        await mw.InvokeAsync(ctx, Env("Development"), Config(), InMemoryDbFactory(("first@db.test", "First User")));

        ctx.User.Identity!.IsAuthenticated.Should().BeTrue();
        ctx.User.FindFirst(ClaimTypes.Email)!.Value.Should().Be("first@db.test");
        nextCalled().Should().BeTrue();
    }

    [Fact]
    public async Task Development_NoConfig_NoUsers_Returns503_AndDoesNotAuthenticate()
    {
        var (mw, nextCalled) = Middleware();
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();

        await mw.InvokeAsync(ctx, Env("Development"), Config(), InMemoryDbFactory(null));

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        ctx.User.Identity!.IsAuthenticated.Should().BeFalse("no phantom identity may be fabricated");
        nextCalled().Should().BeFalse("the request is short-circuited with 503");
    }
}
