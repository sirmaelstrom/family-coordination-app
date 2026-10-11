using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// The /api exception → status mapping (quest 940b4022), through the real host against real Postgres.
/// <para><b>The server-fault case is the negative control.</b> Before the central mapping, <c>RoomsEndpoints.UpdateRoom</c>
/// wrapped the service in <c>catch (InvalidOperationException) → 404</c>, so an EF-internal
/// <see cref="InvalidOperationException"/> (here EF's composite-key guard refusing a changed <c>HouseholdId</c>,
/// the tenancy soak blind spot named at <c>ApplicationDbContext.Writes.cs</c>) answered a silent 404. It must answer
/// 500: it is a server fault, not a not-found.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class ApiExceptionMappingTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private const int B = ChoresWebAppFactory.HouseholdBId;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ChoresWebAppFactory _factory = new(postgres);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private sealed record RoomDto(int id, string name);

    [Fact]
    public async Task An_EF_InvalidOperationException_inside_an_endpoint_answers_500_not_404()
    {
        await using var host = _factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(services => services.AddScoped<IRoomService>(sp => new KeyChangingRoomService(
                ActivatorUtilities.CreateInstance<RoomService>(sp),
                sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()))));
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(ChoresWebAppFactory.TestUserHeader, ChoresWebAppFactory.UserAEmail);

        var created = await client.PostAsJsonAsync("/api/rooms/", new { name = "Key guard room", icon = "x", photoPath = (string?)null });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var room = (await created.Content.ReadFromJsonAsync<RoomDto>(Json))!;

        var response = await client.PutAsJsonAsync(
            $"/api/rooms/{room.id}", new { name = KeyChangingRoomService.ChangeHouseholdId, icon = "x", photoPath = (string?)null });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError,
            "EF's key guard is a server fault; a catch (InvalidOperationException) → 404 hid it. Body: {0}", body);
        body.Should().Be("{\"message\":\"Something went wrong on our end.\"}", "nothing from the exception reaches the wire");
    }

    /// <summary>For a marker name, changes the loaded room's <c>HouseholdId</c> and saves: EF refuses it as an
    /// <see cref="InvalidOperationException"/> from its key guard. Everything else is the real service.</summary>
    private sealed class KeyChangingRoomService(IRoomService inner, IDbContextFactory<ApplicationDbContext> dbFactory) : IRoomService
    {
        public const string ChangeHouseholdId = "change the household id";

        public async Task<Room> UpdateRoomAsync(
            int householdId, int roomId, string name, string icon, string? photoPath, CancellationToken cancellationToken = default)
        {
            if (name == ChangeHouseholdId)
            {
                await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
                var room = await db.Rooms.SingleAsync(r => r.RoomId == roomId, cancellationToken);
                room.HouseholdId = B;
                await db.SaveChangesAsync(cancellationToken);
            }
            return await inner.UpdateRoomAsync(householdId, roomId, name, icon, photoPath, cancellationToken);
        }

        public Task<List<Room>> ListRoomsAsync(int householdId, CancellationToken cancellationToken = default) =>
            inner.ListRoomsAsync(householdId, cancellationToken);

        public Task<Room?> GetRoomAsync(int householdId, int roomId, CancellationToken cancellationToken = default) =>
            inner.GetRoomAsync(householdId, roomId, cancellationToken);

        public Task<Room> CreateRoomAsync(int householdId, string name, string icon, string? photoPath = null, CancellationToken cancellationToken = default) =>
            inner.CreateRoomAsync(householdId, name, icon, photoPath, cancellationToken);

        public Task DeleteRoomAsync(int householdId, int roomId, CancellationToken cancellationToken = default) =>
            inner.DeleteRoomAsync(householdId, roomId, cancellationToken);

        public Task ReorderAsync(int householdId, IReadOnlyList<int> orderedRoomIds, CancellationToken cancellationToken = default) =>
            inner.ReorderAsync(householdId, orderedRoomIds, cancellationToken);
    }
}
