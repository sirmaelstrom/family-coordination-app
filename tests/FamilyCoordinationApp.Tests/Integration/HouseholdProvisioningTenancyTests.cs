using System.Net;
using System.Net.Http.Json;
using FamilyCoordinationApp.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// Admin approve and admin create under the read switch (fca-household-scope WP-02 step 7a, V7), over HTTP as the
/// site admin (Alice). Both routes are unmarked (site-admin, global), and both seed the new household's rooms and
/// chores AFTER the commit inside a swallowing <c>try/catch</c> (left as is, MN8). So the proof is the OUTCOME
/// (E11): the new household has rooms and chores, read back through an Unfiltered context.
/// <para>Negative control: close the <c>RunAs(household.Id)</c> right after the commit, and the seed helper's reads
/// throw <c>TenantNotSetException</c> into the catch, so no rooms or chores appear.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class HouseholdProvisioningTenancyTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private sealed record SummaryWire(int householdId, string name, int memberCount, string createdAt);

    [Fact]
    public async Task Approve_seeds_the_new_households_rooms_and_chores()
    {
        int requestId;
        await using (var seed = await Unfiltered().CreateDbContextAsync())
        {
            // The base fixture has no pending request (SettingsAdminEndpointTests.SeedRequestAsync's logic, duplicated).
            var request = new HouseholdRequest
            {
                Email = "approved-owner@example.com",
                DisplayName = "Approved Owner",
                HouseholdName = "Approved Tenancy Home",
                Status = HouseholdRequestStatus.Pending,
                RequestedAt = DateTime.UtcNow,
            };
            seed.HouseholdRequests.Add(request);
            await seed.SaveChangesAsync();
            requestId = request.Id;
        }

        var response = await _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail)
            .PostAsync($"/api/settings/household-requests/{requestId}/approve", null);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        await ShouldHaveRoomsAndChores((await response.Content.ReadFromJsonAsync<SummaryWire>())!.householdId);
    }

    [Fact]
    public async Task Admin_create_seeds_the_new_households_rooms_and_chores()
    {
        var response = await _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail).PostAsJsonAsync(
            "/api/settings/households",
            new { householdName = "Created Tenancy Home", ownerEmail = "created-owner@example.com", ownerDisplayName = "Created Owner" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        await ShouldHaveRoomsAndChores((await response.Content.ReadFromJsonAsync<SummaryWire>())!.householdId);
    }

    private PostgresDbContextFactory Unfiltered() => new(_factory.ConnectionString);

    private async Task ShouldHaveRoomsAndChores(int householdId)
    {
        await using var db = await Unfiltered().CreateDbContextAsync();
        (await db.Rooms.CountAsync(r => r.HouseholdId == householdId))
            .Should().BePositive("the post-commit seed ran under the new household's RunAs");
        (await db.Chores.CountAsync(c => c.HouseholdId == householdId)).Should().BePositive();
    }
}
