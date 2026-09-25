using FamilyCoordinationApp.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// First-run setup under the read switch (fca-household-scope WP-02 step 7a, V7). On an unseeded database, and
/// IN-REQUEST with the tenant Unset (the <c>/setup</c> page is unmarked), <c>SetupService.CreateHouseholdAsync</c>
/// must produce the household with its admin, default categories, rooms and chores.
/// <para>Its <c>RunAs(household.Id)</c> has to stay open through BOTH seed helpers: they open their own contexts
/// from the same scoped factory and read (their idempotency checks). Negative control: close the <c>RunAs</c>
/// right after the users save, and the first helper's read throws <c>TenantNotSetException</c>.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class FirstRunSetupTenancyTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task CreateHousehold_in_request_on_an_unseeded_database_seeds_everything_for_the_new_household()
    {
        var connectionString = await postgres.CreateDatabaseConnectionStringAsync();
        await using var factory = new DevAuthTestingWebAppFactory(connectionString); // startup migrates; no household

        int householdId;
        using (var scope = factory.Services.CreateScope())
        {
            var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
            accessor.HttpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            try
            {
                var (household, _) = await scope.ServiceProvider.GetRequiredService<SetupService>()
                    .CreateHouseholdAsync("First Home", "first@home.test", "First Owner", "google-first");
                householdId = household.Id;
            }
            finally
            {
                accessor.HttpContext = null;
            }
        }

        await using var db = await new PostgresDbContextFactory(connectionString).CreateDbContextAsync();
        (await db.Households.CountAsync(h => h.Id == householdId)).Should().Be(1);
        (await db.Users.SingleAsync(u => u.HouseholdId == householdId)).Email.Should().Be("first@home.test");
        (await db.Categories.CountAsync(c => c.HouseholdId == householdId)).Should().Be(9, "SeedDefaultCategoriesAsync ran under the RunAs");
        (await db.Rooms.CountAsync(r => r.HouseholdId == householdId)).Should().BePositive("SeedChoresAndRoomsAsync ran under the RunAs");
        (await db.Chores.CountAsync(c => c.HouseholdId == householdId)).Should().BePositive();
    }
}
