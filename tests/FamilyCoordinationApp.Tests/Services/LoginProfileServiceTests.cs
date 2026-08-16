using System.Security.Claims;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// The profile refresh that used to run inside <c>WhitelistedEmailHandler</c> — i.e. on every authorization
/// evaluation of every request — now runs once per sign-in off the OAuth <c>OnCreatingTicket</c> event.
/// </summary>
public sealed class LoginProfileServiceTests
{
    private static readonly DateTime Now = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    private static ClaimsPrincipal Principal(
        string? email, string? name = null, string? picture = null, string? subject = null)
    {
        var claims = new List<Claim>();
        if (email is not null) claims.Add(new Claim(ClaimTypes.Email, email));
        if (name is not null) claims.Add(new Claim(ClaimTypes.Name, name));
        if (picture is not null) claims.Add(new Claim("urn:google:picture", picture));
        if (subject is not null) claims.Add(new Claim(ClaimTypes.NameIdentifier, subject));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static (LoginProfileService Service, DbContextOptions<ApplicationDbContext> Options) Build(User? seedUser)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        if (seedUser is not null)
        {
            using var seed = new ApplicationDbContext(options);
            seed.Households.Add(new Household { Id = 1, Name = "H", CreatedAt = DateTime.UtcNow });
            seed.Users.Add(seedUser);
            seed.SaveChanges();
        }

        var mock = new Mock<IDbContextFactory<ApplicationDbContext>>();
        mock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        var service = new LoginProfileService(
            mock.Object,
            new FixedTimeProvider(Now),
            NullLogger<LoginProfileService>.Instance);

        return (service, options);
    }

    private static User Seeded() => new()
    {
        Id = 1,
        HouseholdId = 1,
        Email = "alice@a.test",
        DisplayName = "Alice Anderson",
        Initials = "??",
        IsWhitelisted = true,
        CreatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task Refresh_CopiesTheGoogleClaimsOntoTheUserRow()
    {
        var (service, options) = Build(Seeded());

        await service.RefreshAsync(Principal(
            "alice@a.test", "Alice Anderson", "https://pic.test/a.jpg", subject: "google-sub-1"));

        await using var db = new ApplicationDbContext(options);
        var user = await db.Users.SingleAsync();
        user.LastLoginAt.Should().Be(Now);
        user.PictureUrl.Should().Be("https://pic.test/a.jpg");
        user.Initials.Should().Be("AA");
        user.GoogleId.Should().Be("google-sub-1", "a first Google sign-in is the only thing that can fill it");
    }

    [Fact]
    public async Task Refresh_KeepsWhatGoogleDidNotSend()
    {
        // An account with no Google photo sends no picture claim; an invited user's DisplayName is theirs, not
        // Google's; and an existing GoogleId is that account's identity under a filtered unique index.
        var seeded = Seeded();
        seeded.DisplayName = "bob";
        seeded.PictureUrl = "https://pic.test/existing.jpg";
        seeded.GoogleId = "original-sub";
        var (service, options) = Build(seeded);

        await service.RefreshAsync(Principal(
            "alice@a.test", "Robert Smith", picture: null, subject: "different-sub"));

        await using var db = new ApplicationDbContext(options);
        var user = await db.Users.SingleAsync();
        user.PictureUrl.Should().Be("https://pic.test/existing.jpg");
        user.GoogleId.Should().Be("original-sub");
        user.DisplayName.Should().Be("bob");
        user.Initials.Should().Be("B", "initials follow the stored DisplayName, not the Google name claim");
    }

    [Fact]
    public async Task Refresh_SwallowsADatabaseFailure_SoTheSignInStillSucceeds()
    {
        var throwing = new Mock<IDbContextFactory<ApplicationDbContext>>();
        throwing.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is down"));
        var service = new LoginProfileService(
            throwing.Object, new FixedTimeProvider(Now), NullLogger<LoginProfileService>.Instance);

        var act = async () => await service.RefreshAsync(Principal("alice@a.test", "Alice Anderson"));

        await act.Should().NotThrowAsync("a profile refresh must never fail the sign-in it hangs off");
    }

    [Fact]
    public async Task Refresh_WithNoMatchingUser_IsANoOp()
    {
        // A sign-in can precede the account: first-run setup, or a household request still pending approval.
        var (service, options) = Build(Seeded());

        await service.RefreshAsync(Principal("stranger@a.test", "Stranger"));

        await using var db = new ApplicationDbContext(options);
        (await db.Users.SingleAsync()).Initials.Should().Be("??");
    }

    [Fact]
    public async Task Refresh_WithNoEmailClaim_NeverTouchesTheDatabase()
    {
        var mock = new Mock<IDbContextFactory<ApplicationDbContext>>();
        var service = new LoginProfileService(
            mock.Object, new FixedTimeProvider(Now), NullLogger<LoginProfileService>.Instance);

        await service.RefreshAsync(Principal(email: null, name: "Nameless"));

        mock.Verify(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
