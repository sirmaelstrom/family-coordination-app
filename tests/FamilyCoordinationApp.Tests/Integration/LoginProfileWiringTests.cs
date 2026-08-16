using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// The profile refresh moved out of the authorization handler and onto the Google <c>OnCreatingTicket</c> event,
/// which nothing else can reach: the local dev stack never performs an OAuth sign-in (the dev-auth bypass hands
/// out an identity directly) and <see cref="ChoresWebAppFactory"/> swaps the real schemes for a test one. So the
/// wiring is exercised here by resolving the host's CONFIGURED <c>GoogleOptions</c> and firing the event it
/// carries — if the hook is ever dropped from <c>Program.cs</c>, the write stops happening and this goes red.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class LoginProfileWiringTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task TheGoogleCreatingTicketEvent_RefreshesTheSignedInUsersProfile()
    {
        var connectionString = await postgres.CreateDatabaseConnectionStringAsync();
        await using var factory = new DevAuthTestingWebAppFactory(connectionString);
        await factory.MigrateAndSeedAsync();

        var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var seed = await dbFactory.CreateDbContextAsync())
        {
            seed.Users.Add(new User
            {
                HouseholdId = 1,
                Email = "signin@a.test",
                DisplayName = "Sign In",
                Initials = "",
                IsWhitelisted = true,
                CreatedAt = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<GoogleOptions>>()
            .Get(GoogleDefaults.AuthenticationScheme);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Email, "signin@a.test"),
                new Claim(ClaimTypes.Name, "Sign In"),
                new Claim("urn:google:picture", "https://pic.test/s.jpg")
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

        await options.Events.CreatingTicket(context);

        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Email == "signin@a.test");
        user.Initials.Should().Be("SI");
        user.PictureUrl.Should().Be("https://pic.test/s.jpg");
        user.LastLoginAt.Should().NotBeNull();
    }
}
