using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// End-to-end coverage for one-off chore due dates (feedback #5) through the real HTTP endpoints + the real
/// <see cref="FamilyCoordinationApp.Services.ChoreStatusCalculator"/> against real Postgres. The host injects a
/// FIXED clock (<see cref="ChoresWebAppFactory.FixedNowUtc"/> = local <b>Sunday 2026-06-07</b> in
/// America/Chicago), so "future / today / past" are deterministic relative to that local date. Proves the
/// server-authoritative <c>dueState</c> the island renders (M5):
/// <list type="bullet">
///   <item><description>a FUTURE due date ⇒ <c>scheduled</c> (the fix — was the bare <c>notDue</c>/"On track",
///   now consistent with future-dated fixed chores; the card renders "Scheduled");</description></item>
///   <item><description>a due date of TODAY ⇒ <c>dueToday</c>;</description></item>
///   <item><description>a PAST due date ⇒ <c>overdue</c>;</description></item>
///   <item><description>NO due date ⇒ <c>notDue</c> (a one-off with no anchor never applies pressure).</description></item>
/// </list>
/// The <c>anchorDate</c> set at create time round-trips back on the projected card (M9 — one projection).
/// <para><b>Contract note:</b> the chore identifier serializes as <c>id</c>; <c>recurrenceMode</c> serializes
/// PascalCase on the response (entity-enum <c>.ToString()</c>) while the create request accepts camelCase;
/// <c>dueState</c>/<c>colorTier</c> are camelCase (JsonStringEnumConverter). These mirror the frozen
/// <c>types.ts</c> board contract.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class ChoreOneOffDueDateTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient ClientA => _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

    // Only the fields these tests assert on (camelCase via JsonSerializerDefaults.Web). `id` (not choreId);
    // `recurrenceMode` is PascalCase on the response.
    private sealed record ChoreCard(int id, string recurrenceMode, string? anchorDate, string dueState, string colorTier);
    private sealed record Board(List<ChoreCard> chores);

    private async Task<ChoreCard> CreateOneOffAsync(HttpClient client, string name, string? anchorDate)
    {
        var resp = await client.PostAsJsonAsync("/api/chores/", new
        {
            name,
            recurrenceMode = "oneOff",
            anchorDate,
            effortTier = "standard"
        }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var card = await resp.Content.ReadFromJsonAsync<ChoreCard>(Json);
        card.Should().NotBeNull();
        return card!;
    }

    [Fact]
    public async Task OneOff_FutureDueDate_IsScheduled_AndAnchorRoundTrips()
    {
        // FixedNowUtc is local Sun 2026-06-07; 2026-06-10 is in the future.
        var card = await CreateOneOffAsync(ClientA, "Future one-off", "2026-06-10");

        card.recurrenceMode.Should().Be("OneOff");
        card.anchorDate.Should().Be("2026-06-10", "the one-off due date round-trips on the projected card");
        card.dueState.Should().Be("scheduled",
            "a future-dated one-off has a concrete pending slot — it reads as Scheduled, not the bare On-track");
        card.colorTier.Should().Be("fresh");
    }

    [Fact]
    public async Task OneOff_DueToday_IsDueToday()
    {
        // 2026-06-07 is the fixed clock's local date.
        var card = await CreateOneOffAsync(ClientA, "Today one-off", "2026-06-07");

        card.dueState.Should().Be("dueToday");
        card.colorTier.Should().Be("due");
    }

    [Fact]
    public async Task OneOff_PastDueDate_IsOverdue()
    {
        var card = await CreateOneOffAsync(ClientA, "Past one-off", "2026-06-01");

        card.dueState.Should().Be("overdue");
        card.colorTier.Should().Be("overdue");
    }

    [Fact]
    public async Task OneOff_NoDueDate_IsNotDue()
    {
        var card = await CreateOneOffAsync(ClientA, "Someday one-off", anchorDate: null);

        card.anchorDate.Should().BeNull();
        card.dueState.Should().Be("notDue", "a one-off with no due date never applies due pressure");
    }

    [Fact]
    public async Task OneOff_FutureDueDate_ScheduledStateSurvivesTheBoardProjection()
    {
        // Create via the mutation projection, then re-read the board (the OTHER projection) — M9 says they
        // are the same projection, so the future one-off must read 'scheduled' on the board too.
        var created = await CreateOneOffAsync(ClientA, "Future one-off on board", "2026-06-12");

        var board = await ClientA.GetFromJsonAsync<Board>("/api/chores/board", Json);
        board.Should().NotBeNull();
        var onBoard = board!.chores.SingleOrDefault(c => c.id == created.id);
        onBoard.Should().NotBeNull("the active future one-off must appear on the board");
        onBoard!.dueState.Should().Be("scheduled");
        onBoard.anchorDate.Should().Be("2026-06-12");
    }
}
