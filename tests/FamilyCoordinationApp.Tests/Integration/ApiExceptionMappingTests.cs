using System.Collections.Concurrent;
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
using Microsoft.Extensions.Logging;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// The /api exception → status mapping (quest 940b4022) and the one concurrency path (quest 8cf3c806), through the
/// real host against real Postgres.
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

    private HttpClient ClientA => _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

    private sealed record RoomDto(int id, string name);

    private static async Task<string> MessageOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json", "body: {0}", body);
        return JsonDocument.Parse(body).RootElement.GetProperty("message").GetString()!;
    }

    // ── Server faults answer 500 (P2) ───────────────────────────────────────────────

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

    // ── Domain refusals answer their own status with a specific, id-free message ───────

    public static TheoryData<string, string, string?, string> NotFoundRoutes() => new()
    {
        // Each of these used to be a per-endpoint catch; four of them answered a bodiless 404 the backfill filled in.
        { "PUT", "/api/rooms/999999", """{"name":"x","icon":"x","photoPath":null}""", "Room not found." },
        { "DELETE", "/api/rooms/999999", null, "Room not found." },
        { "DELETE", "/api/recipes/999999", null, "Recipe not found." },
        { "POST", "/api/chores/999999/claim", """{"version":1}""", "Chore not found." },
        { "DELETE", "/api/meal-plan/entries/999999/1", """{"version":1}""", "Meal entry not found." },
        { "POST", "/api/shopping-lists/999999/actions/toggle-favorite", null, "Shopping list not found." },
    };

    [Theory]
    [MemberData(nameof(NotFoundRoutes))]
    public async Task A_NotFoundException_answers_404_with_its_resource_message(string method, string url, string? json, string expected)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (json is not null) request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await ClientA.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await MessageOf(response)).Should().Be(expected);
    }

    [Fact]
    public async Task A_DomainValidationException_answers_400_with_its_message()
    {
        var response = await ClientA.PutAsJsonAsync("/api/chores/digest-settings", new
        {
            enabled = true,
            cadence = "weekly",
            sendDayOfWeek = "sunday",
            sendHourLocal = 99,
        }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await MessageOf(response)).Should().Be("SendHourLocal must be in [0, 23] but was 99.");
    }

    [Fact]
    public async Task A_domain_refusal_is_not_logged_as_an_unhandled_exception()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = _factory.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(logs)));
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(ChoresWebAppFactory.TestUserHeader, ChoresWebAppFactory.UserAEmail);

        (await client.DeleteAsync("/api/rooms/999999")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        logs.Entries.Where(e => e.Exception is DomainException).Should().BeEmpty(
            "an expected refusal is not an unhandled exception (SuppressDiagnosticsCallback); the endpoint catches it replaced never logged");
    }

    // ── Concurrency: one path, one 409, a fixed message ──────────────────────────────

    [Theory]
    [InlineData("""{"version":123456}""", "a stale token")]
    [InlineData("""{}""", "a missing token (binds to 0: fail-safe)")]
    public async Task A_concurrency_conflict_answers_409_with_the_fixed_message_and_no_ids(string json, string because)
    {
        var response = await ClientA.PostAsync($"/api/chores/{ChoresWebAppFactory.PileChoreAId}/claim",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, because);
        (await MessageOf(response)).Should().Be(ConcurrencyConflictException.FixedMessage);
        body.Should().NotContain("Chore 1", "the 409 used to echo the service's internal ids");
    }

    /// <summary>
    /// The server-side read-modify-write path (invite accept/invalidate, calendar-token revoke): no client token, so the
    /// check is against the loaded xmin. A writer landing between load and save used to raise a bare
    /// <see cref="DbUpdateConcurrencyException"/> (a 500); it is a <see cref="ConcurrencyConflictException"/> (409) now.
    /// </summary>
    [Fact]
    public async Task A_write_between_load_and_save_on_a_versioned_row_is_a_ConcurrencyConflictException()
    {
        var db = new PostgresDbContextFactory(_factory.ConnectionString);
        int inviteId;
        await using (var seed = await db.CreateDbContextAsync())
        {
            var invite = new HouseholdInvite
            {
                HouseholdId = ChoresWebAppFactory.HouseholdAId,
                InviteCode = "CNFLCT",
                CreatedByUserId = ChoresWebAppFactory.UserAId,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(1),
            };
            seed.HouseholdInvites.Add(invite);
            await seed.SaveChangesAsync();
            inviteId = invite.Id;
        }

        await using var loser = await db.CreateDbContextAsync();
        var stale = await loser.HouseholdInvites.SingleAsync(i => i.Id == inviteId);

        await using (var winner = await db.CreateDbContextAsync())
        {
            (await winner.HouseholdInvites.SingleAsync(i => i.Id == inviteId)).IsUsed = true;
            await winner.SaveChangesOrConflictAsync(CancellationToken.None);
        }

        stale.UsedAt = DateTime.UtcNow;
        var act = () => loser.SaveChangesOrConflictAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<ConcurrencyConflictException>())
            .WithInnerException<DbUpdateConcurrencyException>();
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

    /// <summary>Records every log entry the host writes, with its exception.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public sealed record Entry(string Category, LogLevel Level, Exception? Exception);

        public ConcurrentQueue<Entry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

        public void Dispose() { }

        private sealed class Logger(string category, ConcurrentQueue<Entry> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                sink.Enqueue(new Entry(category, logLevel, exception));
        }
    }
}
