using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// End-to-end coverage for the room manager (chores v1.2 — PR #21) through the real <c>/api/rooms</c>
/// endpoints against real Postgres. Exercises the full CRUD + reorder surface the island drives, plus the
/// load-bearing delete semantic: deleting a room does NOT delete its chores — it reassigns them to
/// <c>RoomId = null</c> ("General"), which the board renders under the General bucket. Tenant scoping is
/// inherited from the shared <see cref="ChoresWebAppFactory"/> seed (every handler resolves HouseholdId from
/// the caller, M1).
/// <para><b>Contract note:</b> <see cref="RoomDto"/> is <c>{ id, name, icon, photoPath, sortOrder }</c> — the
/// identifier serializes as <c>id</c>, and rooms carry NO concurrency token (delete takes no version body and
/// is last-write-wins; there is no 409 path here, unlike chores). The seed adds no rooms, so created rooms
/// start at id 1.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class RoomCrudTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient ClientA => _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

    private sealed record RoomDto(int id, string name, string icon, string? photoPath, int sortOrder);
    private sealed record ChoreCard(int id, int? roomId);
    private sealed record Board(List<ChoreCard> chores);

    private async Task<RoomDto> CreateRoomAsync(HttpClient client, string name, string? icon = null)
    {
        var resp = await client.PostAsJsonAsync("/api/rooms/", new { name, icon, photoPath = (string?)null }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<RoomDto>(Json))!;
    }

    private async Task<List<RoomDto>> GetRoomsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<RoomDto>>("/api/rooms/", Json))!;

    [Fact]
    public async Task CreateRoom_ThenGet_AppearsWithTrimmedNameAndIcon()
    {
        var client = ClientA;

        var created = await CreateRoomAsync(client, "  Garage  ", icon: "🚗");
        created.name.Should().Be("Garage", "the service trims the room name");
        created.icon.Should().Be("🚗");

        var rooms = await GetRoomsAsync(client);
        rooms.Should().Contain(r => r.id == created.id && r.name == "Garage");
    }

    [Fact]
    public async Task CreateTwoRooms_ThenReorder_PersistsNewOrder()
    {
        var client = ClientA;

        var first = await CreateRoomAsync(client, "Kitchen-A");
        var second = await CreateRoomAsync(client, "Bathroom-A");
        // Fresh rooms get increasing SortOrder, so 'first' currently sorts before 'second'.
        first.sortOrder.Should().BeLessThan(second.sortOrder);

        // Reorder so 'second' comes before 'first'.
        var reorderResp = await client.PostAsJsonAsync("/api/rooms/reorder",
            new { orderedRoomIds = new[] { second.id, first.id } }, Json);
        reorderResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var rooms = await GetRoomsAsync(client);
        var secondSort = rooms.Single(r => r.id == second.id).sortOrder;
        var firstSort = rooms.Single(r => r.id == first.id).sortOrder;
        secondSort.Should().BeLessThan(firstSort, "the reorder must move 'second' ahead of 'first'");
    }

    [Fact]
    public async Task UpdateRoom_RenameAndIcon_RoundTrips()
    {
        var client = ClientA;
        var created = await CreateRoomAsync(client, "Office-A", icon: "📎");

        var resp = await client.PutAsJsonAsync($"/api/rooms/{created.id}",
            new { name = "Home Office", icon = "🖥️", photoPath = (string?)null }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await resp.Content.ReadFromJsonAsync<RoomDto>(Json))!;

        updated.name.Should().Be("Home Office");
        updated.icon.Should().Be("🖥️");

        var rooms = await GetRoomsAsync(client);
        rooms.Single(r => r.id == created.id).name.Should().Be("Home Office");
    }

    [Fact]
    public async Task UpdateRoom_BlankName_Returns400()
    {
        var client = ClientA;
        var created = await CreateRoomAsync(client, "Pantry-A");

        var resp = await client.PutAsJsonAsync($"/api/rooms/{created.id}",
            new { name = "   ", icon = (string?)null, photoPath = (string?)null }, Json);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a blank room name is rejected, not coerced");
    }

    [Fact]
    public async Task DeleteRoom_ReassignsItsChoresToGeneral_NotDeletingThem()
    {
        var client = ClientA;
        var room = await CreateRoomAsync(client, "Mudroom-A");

        // Create a chore IN that room.
        var createChore = await client.PostAsJsonAsync("/api/chores/", new
        {
            name = "Sweep the mudroom",
            roomId = room.id,
            recurrenceMode = "flexible",
            intervalDays = 7,
            effortTier = "quick"
        }, Json);
        createChore.StatusCode.Should().Be(HttpStatusCode.Created);
        var chore = (await createChore.Content.ReadFromJsonAsync<ChoreCard>(Json))!;
        chore.roomId.Should().Be(room.id);

        // Delete the room (no body — rooms have no concurrency token).
        var deleteResp = await client.DeleteAsync($"/api/rooms/{room.id}");
        deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The room is gone...
        var rooms = await GetRoomsAsync(client);
        rooms.Should().NotContain(r => r.id == room.id);

        // ...but its chore survives, reassigned to General (roomId == null), still on the board.
        var board = await client.GetFromJsonAsync<Board>("/api/chores/board", Json);
        var survivor = board!.chores.SingleOrDefault(c => c.id == chore.id);
        survivor.Should().NotBeNull("deleting a room must not delete its chores");
        survivor!.roomId.Should().BeNull("the orphaned chore is reassigned to General");
    }

    [Fact]
    public async Task DeleteRoom_MultiRoomChore_KeepsItsOtherRoom_NotGeneral()
    {
        // Phase 13 (M:N): a chore in TWO rooms survives a room delete by keeping the other room — it does
        // NOT fall to General. Until WP-04 the board carries only the single-room shim, so we assert the
        // survivor reads the remaining room (the min remaining membership).
        var client = ClientA;
        var roomA = await CreateRoomAsync(client, "RoomA-multi");
        var roomB = await CreateRoomAsync(client, "RoomB-multi");

        var createChore = await client.PostAsJsonAsync("/api/chores/", new
        {
            name = "Two-room survivor",
            roomIds = new[] { roomA.id, roomB.id },
            recurrenceMode = "flexible",
            intervalDays = 7,
            effortTier = "quick"
        }, Json);
        createChore.StatusCode.Should().Be(HttpStatusCode.Created);
        var chore = (await createChore.Content.ReadFromJsonAsync<ChoreCard>(Json))!;

        // Delete room A (created first → lower id, so B is the min remaining membership).
        var deleteResp = await client.DeleteAsync($"/api/rooms/{roomA.id}");
        deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var board = await client.GetFromJsonAsync<Board>("/api/chores/board", Json);
        var survivor = board!.chores.SingleOrDefault(c => c.id == chore.id);
        survivor.Should().NotBeNull("a multi-room chore must survive a room delete");
        survivor!.roomId.Should().Be(roomB.id, "it keeps its other room rather than falling to General");
    }

    // NOTE: a "delete a nonexistent room" case is intentionally NOT covered here. The endpoint returns an
    // empty 404, which the app-global UseStatusCodePagesWithReExecute("/not-found") re-executes through the
    // Blazor not-found page — and because re-execution preserves the DELETE verb against a GET-only page, the
    // wire status is 405, not 404/400. That is pre-existing global middleware behavior (the same applies to the
    // shopping-list endpoints), not room-manager behavior, so asserting on it here would only pin plumbing.
}
