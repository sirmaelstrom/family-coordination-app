using FamilyCoordinationApp.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// Authorization runs on every request, so it must not write. <c>WhitelistedEmailHandler</c> used to refresh the
/// caller's profile row (<c>LastLoginAt</c> / <c>PictureUrl</c> / <c>Initials</c> + <c>SaveChangesAsync</c>) each
/// time it evaluated — that refresh now happens once per sign-in in <see cref="LoginProfileService"/>.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class AuthorizationIsReadOnlyTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task AuthorizedRequests_LeaveTheCallersProfileRowUntouched()
    {
        var db = new PostgresDbContextFactory(_factory.ConnectionString);
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        // Give the row a non-null PictureUrl first. The seed leaves it null, so asserting "still null" after the
        // requests would hold whether or not anything wrote — the assertion has to be able to fail.
        const string avatar = "https://pic.test/alice.jpg";
        await using (var seed = await db.CreateDbContextAsync())
        {
            var row = await seed.Users.SingleAsync(u => u.Id == ChoresWebAppFactory.UserAId);
            row.PictureUrl = avatar;
            await seed.SaveChangesAsync();
        }

        for (var i = 0; i < 3; i++)
            (await client.GetAsync("/api/me")).EnsureSuccessStatusCode();

        await using var context = await db.CreateDbContextAsync();
        var user = await context.Users.AsNoTracking()
            .SingleAsync(u => u.Id == ChoresWebAppFactory.UserAId);

        // Against the pre-fix handler the first of these requests rewrote all three: the test principal's name
        // claim is the email, so "AA" became "A"; the absent picture claim nulled PictureUrl; LastLoginAt was
        // stamped with "now".
        user.Initials.Should().Be("AA");
        user.PictureUrl.Should().Be(avatar);
        user.LastLoginAt.Should().BeNull();
    }
}
