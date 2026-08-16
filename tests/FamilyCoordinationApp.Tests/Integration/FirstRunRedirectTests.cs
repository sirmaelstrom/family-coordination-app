using System.Net;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// The un-set-up app had no coverage at all, and <c>SetupCompletionLatch</c> is new logic on exactly that path.
/// A one-way latch's dangerous failure is latching the WRONG answer — freeze <c>false</c> and the app can never
/// leave first-run setup; latch at boot and the redirect never fires. This walks the transition: no household →
/// every non-skipped path 302s to <c>/setup</c>; create one → the same path stops redirecting, without a restart.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class FirstRunRedirectTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task WithNoHousehold_EverythingRedirectsToSetup_AndStopsOnceOneExists()
    {
        var connectionString = await postgres.CreateDatabaseConnectionStringAsync();
        // Deliberately NOT seeded: startup migrates the schema, so the database is live but has no household.
        await using var factory = new DevAuthTestingWebAppFactory(connectionString);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var beforeSetup = await client.GetAsync("/api/me");
        beforeSetup.StatusCode.Should().Be(HttpStatusCode.Redirect);
        beforeSetup.Headers.Location!.OriginalString.Should().Be("/setup");

        // Repeat: a latch that froze the FALSE answer would be indistinguishable here, which is why the
        // transition below is the actual assertion.
        (await client.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.Redirect);

        var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Households.Add(new Household { Name = "First household", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        // Same process, no restart. 401 rather than 302 means the request reached the authorization pipeline —
        // i.e. the setup gate let it past (anonymous /api in a non-Development environment answers 401).
        var afterSetup = await client.GetAsync("/api/me");
        afterSetup.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
