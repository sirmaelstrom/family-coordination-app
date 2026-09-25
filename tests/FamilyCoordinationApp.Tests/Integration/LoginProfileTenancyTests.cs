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
/// The sign-in profile write under the read switch (fca-household-scope WP-02 step 7a, V7). Arranged as
/// <see cref="LoginProfileWiringTests"/>, but IN-REQUEST: <c>IHttpContextAccessor.HttpContext</c> is set, so the
/// tenant is Unset-in-request exactly as in a real OAuth callback. Out of request, the test host's
/// <c>OutOfRequest=Unfiltered</c> would mask a missing bypass (E11).
/// <para><c>RefreshAsync</c> swallows every exception, so the proof is the OUTCOME: the profile persisted.
/// Negative control: without the user read's Tenant bypass, that read throws inside the catch and nothing is
/// written. The <c>RunAs</c> control belongs to WP-03, where writes are checked.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class LoginProfileTenancyTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task The_CreatingTicket_event_in_request_persists_the_profile()
    {
        var connectionString = await postgres.CreateDatabaseConnectionStringAsync();
        await using var factory = new DevAuthTestingWebAppFactory(connectionString);
        await factory.MigrateAndSeedAsync();

        var unfiltered = new PostgresDbContextFactory(connectionString);
        await using (var seed = await unfiltered.CreateDbContextAsync())
        {
            seed.Users.Add(new User
            {
                HouseholdId = 1,
                Email = "tenancy-signin@a.test",
                DisplayName = "Tenancy Signin",
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
                new Claim(ClaimTypes.Email, "tenancy-signin@a.test"),
                new Claim("urn:google:picture", "https://pic.test/t.jpg"),
                new Claim(ClaimTypes.NameIdentifier, "google-subject-tenancy")
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
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Email == "tenancy-signin@a.test");
        user.PictureUrl.Should().Be("https://pic.test/t.jpg", "the in-request refresh must read the user past the Tenant filter");
        user.GoogleId.Should().Be("google-subject-tenancy");
        user.LastLoginAt.Should().NotBeNull();
    }
}
