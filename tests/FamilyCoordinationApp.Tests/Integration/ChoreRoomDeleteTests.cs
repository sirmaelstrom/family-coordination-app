using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// Real-Postgres guard for the Phase 13 chore-delete membership cleanup (WP-02, constraint M4). The
/// <c>ChoreRoom → Chore</c> FK is <c>ClientNoAction</c> (NO ACTION), so deleting a chore that has room
/// memberships must remove those rows FIRST in service code — otherwise Postgres throws FK 23503 and the
/// DELETE surfaces as a 500. The InMemory unit suite cannot observe this (no FK enforcement); only real
/// Postgres does, which is exactly the blindspot the council flagged.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class ChoreRoomDeleteTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient ClientA => _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

    private sealed record RoomDto(int id, string name);
    private sealed record ChoreCard(int id, uint version);

    private async Task<int> CreateRoomAsync(HttpClient client, string name)
    {
        var resp = await client.PostAsJsonAsync("/api/rooms/", new { name, icon = (string?)null, photoPath = (string?)null }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<RoomDto>(Json))!.id;
    }

    private async Task<int> MembershipCountAsync(int choreId)
    {
        await using var ctx = new PostgresDbContextFactory(_factory.ConnectionString).CreateDbContext();
        return await ctx.ChoreRooms.CountAsync(cr =>
            cr.HouseholdId == ChoresWebAppFactory.HouseholdAId && cr.ChoreId == choreId);
    }

    [Fact]
    public async Task DeleteChore_WithRoomMemberships_Returns204_AndRemovesMembershipRows()
    {
        var client = ClientA;
        var roomA = await CreateRoomAsync(client, "Room-A-del");
        var roomB = await CreateRoomAsync(client, "Room-B-del");

        // Create a chore in TWO rooms via the new roomIds field (WP-02 writes a ChoreRoom row per id).
        var createResp = await client.PostAsJsonAsync("/api/chores/", new
        {
            name = "Two-room chore to delete",
            roomIds = new[] { roomA, roomB },
            recurrenceMode = "flexible",
            intervalDays = 7,
            effortTier = "quick"
        }, Json);
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var chore = (await createResp.Content.ReadFromJsonAsync<ChoreCard>(Json))!;

        (await MembershipCountAsync(chore.id)).Should().Be(2, "the create wrote one ChoreRoom row per room");

        // DELETE with the version body — must be 204, NOT a 500 from FK 23503.
        var deleteReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/chores/{chore.id}")
        {
            Content = JsonContent.Create(new { version = chore.version }, options: Json)
        };
        var deleteResp = await client.SendAsync(deleteReq);
        deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "chore-delete removes its memberships first, avoiding FK 23503");

        (await MembershipCountAsync(chore.id)).Should().Be(0, "the delete removed the membership rows");
    }
}
