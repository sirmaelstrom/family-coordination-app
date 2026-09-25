using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Tenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// The Development startup seed under production's out-of-request rule (fca-household-scope WP-02 step 7a, D11/D15).
/// No test host: a real <see cref="TenantContext"/> with no HttpContext and <c>OutOfRequest=Throw</c>, behind the real
/// <see cref="TenantDbContextFactory"/>, is exactly what <c>Program.cs</c>'s startup scope gives the seed.
/// <para>Negative control: without <c>SeedDevelopmentDataAsync</c>'s <c>RunAs(household.Id)</c>, the <c>Users</c>
/// lookup throws <see cref="TenantNotSetException"/>.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class DevSeedTenancyTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task SeedDevelopmentData_out_of_request_with_Throw_seeds_the_first_household()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(await postgres.CreateDatabaseConnectionStringAsync())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;

        int householdId;
        await using (var setup = new ApplicationDbContext(options))
        {
            await setup.Database.MigrateAsync();
            var household = new Household { Name = "Dev Household", CreatedAt = DateTime.UtcNow };
            setup.Households.Add(household);
            await setup.SaveChangesAsync();
            setup.Users.Add(new User
            {
                HouseholdId = household.Id,
                Email = "dev@seed.test",
                DisplayName = "Dev User",
                Initials = "DU",
                IsWhitelisted = true,
                CreatedAt = DateTime.UtcNow
            });
            await setup.SaveChangesAsync();
            householdId = household.Id;
        }

        var tenancy = Microsoft.Extensions.Options.Options.Create(new TenancyOptions { OutOfRequest = OutOfRequestMode.Throw });
        var tenant = new TenantContext(new HttpContextAccessor(), tenancy);
        tenant.IsOutOfRequest.Should().BeTrue("the precondition: production's startup condition, no HttpContext");

        await SeedData.SeedDevelopmentDataAsync(new TenantDbContextFactory(options, tenant, tenancy, TimeProvider.System));

        await using var db = new ApplicationDbContext(options);
        (await db.Rooms.CountAsync(r => r.HouseholdId == householdId)).Should().BePositive();
        (await db.Chores.CountAsync(c => c.HouseholdId == householdId)).Should().BePositive();
        (await db.Categories.CountAsync(c => c.HouseholdId == householdId)).Should().Be(9);
        (await db.Recipes.CountAsync(r => r.HouseholdId == householdId)).Should().BePositive();
    }
}
