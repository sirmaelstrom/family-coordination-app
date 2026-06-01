using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// End-to-end coverage for the edit-chore path (feedback #4/#5) through the real <c>PUT /api/chores/{id}</c>
/// endpoint against real Postgres. The bug class behind feedback #4 was the edit dialog NOT pre-filling the
/// recurrence fields; the load-bearing server guarantee under that UI is that the update path PERSISTS and
/// RE-PROJECTS the recurrence fields, and that the mutation-response card matches the board card (M9 — one
/// projection, no card drift). These tests change a chore's whole recurrence shape and assert the new fields
/// survive both the immediate PUT response and a fresh board read:
/// <list type="bullet">
///   <item><description>flexible → fixed every-N (intervalDays + anchorDate);</description></item>
///   <item><description>flexible → fixed weekly (daysOfWeek);</description></item>
///   <item><description>flexible → one-off with a due date (anchorDate, feedback #5);</description></item>
///   <item><description>a stale version is rejected with 409 (optimistic concurrency on the edit path).</description></item>
/// </list>
/// <para><b>Contract note:</b> the chore id serializes as <c>id</c>; <c>recurrenceMode</c> is PascalCase on the
/// response (<c>OneOff</c>/<c>Fixed</c>/<c>Flexible</c>) while the request accepts camelCase; <c>daysOfWeek</c>
/// is a lowercase CSV (e.g. <c>"monday"</c>).</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class ChoreEditRoundTripTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient ClientA => _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

    private sealed record ChoreCard(
        int id, string name, string recurrenceMode, int? intervalDays, string? anchorDate,
        string? daysOfWeek, string dueState, uint version);

    private sealed record Board(List<ChoreCard> chores);

    private async Task<ChoreCard> CreateFlexibleAsync(string name)
    {
        var resp = await ClientA.PostAsJsonAsync("/api/chores/", new
        {
            name,
            recurrenceMode = "flexible",
            intervalDays = 7,
            effortTier = "standard"
        }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<ChoreCard>(Json))!;
    }

    private async Task<ChoreCard> PutAsync(int choreId, object body)
    {
        var resp = await ClientA.PutAsJsonAsync($"/api/chores/{choreId}", body, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<ChoreCard>(Json))!;
    }

    private async Task<ChoreCard> ReadFromBoardAsync(int choreId)
    {
        var board = await ClientA.GetFromJsonAsync<Board>("/api/chores/board", Json);
        var card = board!.chores.SingleOrDefault(c => c.id == choreId);
        card.Should().NotBeNull("the edited chore must still be on the board");
        return card!;
    }

    [Fact]
    public async Task Edit_FlexibleToFixedEveryN_PersistsIntervalAndAnchor_OnResponseAndBoard()
    {
        var created = await CreateFlexibleAsync("Edit to every-N");

        var updated = await PutAsync(created.id, new
        {
            name = "Edit to every-N",
            recurrenceMode = "fixed",
            intervalDays = 3,
            anchorDate = "2026-06-01",
            effortTier = "standard",
            version = created.version
        });

        updated.recurrenceMode.Should().Be("Fixed");
        updated.intervalDays.Should().Be(3);
        updated.anchorDate.Should().Be("2026-06-01");

        var onBoard = await ReadFromBoardAsync(created.id);
        onBoard.recurrenceMode.Should().Be("Fixed");
        onBoard.intervalDays.Should().Be(3);
        onBoard.anchorDate.Should().Be("2026-06-01", "the edit-response and board projections must agree (M9)");
    }

    [Fact]
    public async Task Edit_FlexibleToFixedWeekly_PersistsDaysOfWeek_OnResponseAndBoard()
    {
        var created = await CreateFlexibleAsync("Edit to weekly");

        var updated = await PutAsync(created.id, new
        {
            name = "Edit to weekly",
            recurrenceMode = "fixed",
            daysOfWeek = "monday",
            effortTier = "standard",
            version = created.version
        });

        updated.recurrenceMode.Should().Be("Fixed");
        updated.daysOfWeek.Should().Be("monday", "feedback #4 — the weekday selection must persist on edit");

        var onBoard = await ReadFromBoardAsync(created.id);
        onBoard.daysOfWeek.Should().Be("monday");
    }

    [Fact]
    public async Task Edit_FlexibleToOneOffWithDueDate_PersistsAnchor_AndReadsScheduled()
    {
        var created = await CreateFlexibleAsync("Edit to one-off");

        // FixedNowUtc is local Sun 2026-06-07; 2026-06-20 is in the future ⇒ scheduled.
        var updated = await PutAsync(created.id, new
        {
            name = "Edit to one-off",
            recurrenceMode = "oneOff",
            anchorDate = "2026-06-20",
            effortTier = "standard",
            version = created.version
        });

        updated.recurrenceMode.Should().Be("OneOff");
        updated.anchorDate.Should().Be("2026-06-20", "feedback #5 — the one-off due date must persist on edit");
        updated.intervalDays.Should().BeNull("switching to one-off clears the flexible interval");
        updated.dueState.Should().Be("scheduled");

        var onBoard = await ReadFromBoardAsync(created.id);
        onBoard.anchorDate.Should().Be("2026-06-20");
        onBoard.dueState.Should().Be("scheduled");
    }

    [Fact]
    public async Task Edit_WithStaleVersion_Returns409_Conflict()
    {
        var created = await CreateFlexibleAsync("Edit conflict");

        // First edit succeeds and advances the version.
        var firstVersion = created.version;
        await PutAsync(created.id, new
        {
            name = "Edit conflict v2",
            recurrenceMode = "flexible",
            intervalDays = 5,
            effortTier = "standard",
            version = firstVersion
        });

        // Re-using the now-stale original version must conflict (xmin optimistic concurrency, M7/M12).
        var staleResp = await ClientA.PutAsJsonAsync($"/api/chores/{created.id}", new
        {
            name = "Edit conflict v3",
            recurrenceMode = "flexible",
            intervalDays = 9,
            effortTier = "standard",
            version = firstVersion
        }, Json);

        staleResp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
