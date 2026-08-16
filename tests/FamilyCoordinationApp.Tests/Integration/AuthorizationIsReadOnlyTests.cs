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

        for (var i = 0; i < 3; i++)
            (await client.GetAsync("/api/me")).EnsureSuccessStatusCode();

        await using var context = await db.CreateDbContextAsync();
        var user = await context.Users.AsNoTracking()
            .SingleAsync(u => u.Id == ChoresWebAppFactory.UserAId);

        // The seeded values. Against the pre-fix handler the first of these requests rewrote all three: the test
        // principal's name claim is the email, so "AA" became "A", the absent picture claim nulled PictureUrl,
        // and LastLoginAt was stamped with "now".
        user.Initials.Should().Be("AA");
        user.PictureUrl.Should().BeNull();
        user.LastLoginAt.Should().BeNull();
    }
}
