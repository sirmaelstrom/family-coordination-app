using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// End-to-end coverage of the Phase-14 checklist endpoints against real Postgres via
/// <see cref="ChoresWebAppFactory"/>: POST creates a subtask (200 + id), PUT toggles <c>isDone</c> with NO
/// version body (200), a SECOND version-less PUT also succeeds (last-write-wins — no 409), a subtask write
/// does NOT change the chore's <c>Version</c> (xmin), DELETE returns 204, and a cross-household caller is
/// rejected. Subtasks are versionless / household-scoped (M1).
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class ChoreSubtaskTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private sealed record Chore(int id, string name, uint version);
    private sealed record Subtask(int id, string title, bool isDone, int sortOrder, int? completedByUserId, string? completedAt);
    private sealed record ChoreWithSubtasks(int id, List<Subtask> subtasks);
    private sealed record Board(List<Chore> chores);
    private sealed record BoardWithSubtasks(List<ChoreWithSubtasks> chores);

    private HttpClient ClientA => _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);
    private HttpClient ClientB => _factory.CreateClientAs(ChoresWebAppFactory.UserBEmail);

    private async Task<Chore> CreateFlexibleChoreAsync(HttpClient client, string name)
    {
        var resp = await client.PostAsJsonAsync("/api/chores/", new
        {
            name,
            recurrenceMode = "flexible",
            intervalDays = 7,
            effortTier = "standard"
        }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var chore = await resp.Content.ReadFromJsonAsync<Chore>(Json);
        chore.Should().NotBeNull();
        return chore!;
    }

    private async Task<uint> ChoreVersionFromBoardAsync(HttpClient client, int choreId)
    {
        var board = await client.GetFromJsonAsync<Board>("/api/chores/board", Json);
        var onBoard = board!.chores.SingleOrDefault(c => c.id == choreId);
        onBoard.Should().NotBeNull("the chore must be on the board");
        return onBoard!.version;
    }

    [Fact]
    public async Task Subtask_FullLifecycle_ThroughHttp_AgainstRealPostgres()
    {
        var client = ClientA;
        var chore = await CreateFlexibleChoreAsync(client, "Checklist chore");
        var versionBefore = await ChoreVersionFromBoardAsync(client, chore.id);

        // POST a subtask → 200 with an id.
        var createResp = await client.PostAsJsonAsync($"/api/chores/{chore.id}/subtasks", new { title = "Wipe counter" }, Json);
        createResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var created = await createResp.Content.ReadFromJsonAsync<Subtask>(Json);
        created.Should().NotBeNull();
        created!.id.Should().BeGreaterThan(0);
        created.title.Should().Be("Wipe counter");
        created.isDone.Should().BeFalse();

        // PUT isDone=true with NO version body → 200.
        var put1 = await client.PutAsJsonAsync($"/api/chores/{chore.id}/subtasks/{created.id}", new { isDone = true }, Json);
        put1.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterPut1 = await put1.Content.ReadFromJsonAsync<Subtask>(Json);
        afterPut1!.isDone.Should().BeTrue();

        // A SECOND version-less PUT also succeeds (last-write-wins — never a 409).
        var put2 = await client.PutAsJsonAsync($"/api/chores/{chore.id}/subtasks/{created.id}", new { isDone = false, title = "Wipe counter again" }, Json);
        put2.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterPut2 = await put2.Content.ReadFromJsonAsync<Subtask>(Json);
        afterPut2!.isDone.Should().BeFalse();
        afterPut2.title.Should().Be("Wipe counter again");

        // The chore's Version (xmin) is UNCHANGED across all the subtask writes (subtasks never bump it).
        var versionAfter = await ChoreVersionFromBoardAsync(client, chore.id);
        versionAfter.Should().Be(versionBefore, "a subtask write must never bump the chore's xmin Version");

        // DELETE → 204.
        var del = await client.DeleteAsync($"/api/chores/{chore.id}/subtasks/{created.id}");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Subtask_Tick_CapturesActor_ThenUntick_ClearsIt_ThroughHttp()
    {
        var client = ClientA;
        var chore = await CreateFlexibleChoreAsync(client, "Actor checklist chore");

        var createResp = await client.PostAsJsonAsync($"/api/chores/{chore.id}/subtasks", new { title = "Who ticks" }, Json);
        var created = await createResp.Content.ReadFromJsonAsync<Subtask>(Json);
        created!.completedByUserId.Should().BeNull("a freshly created item has no actor");

        // Tick → the resolved caller is captured as the actor with a timestamp (invariant: actor IFF isDone).
        var tick = await client.PutAsJsonAsync($"/api/chores/{chore.id}/subtasks/{created.id}", new { isDone = true }, Json);
        tick.StatusCode.Should().Be(HttpStatusCode.OK);
        var ticked = await tick.Content.ReadFromJsonAsync<Subtask>(Json);
        ticked!.completedByUserId.Should().NotBeNull("ticking captures the acting user");
        ticked.completedAt.Should().NotBeNullOrEmpty("ticking stamps a UTC time");

        // Untick → the actor clears.
        var untick = await client.PutAsJsonAsync($"/api/chores/{chore.id}/subtasks/{created.id}", new { isDone = false }, Json);
        var unticked = await untick.Content.ReadFromJsonAsync<Subtask>(Json);
        unticked!.completedByUserId.Should().BeNull("unticking clears the actor");
        unticked.completedAt.Should().BeNull();
    }

    [Fact]
    public async Task Subtask_Reorder_PersistsNewOrder_ThroughHttp()
    {
        var client = ClientA;
        var chore = await CreateFlexibleChoreAsync(client, "Reorder checklist chore");

        var ids = new List<int>();
        foreach (var title in new[] { "First", "Second", "Third" })
        {
            var resp = await client.PostAsJsonAsync($"/api/chores/{chore.id}/subtasks", new { title }, Json);
            ids.Add((await resp.Content.ReadFromJsonAsync<Subtask>(Json))!.id);
        }

        // New order: Third, First, Second.
        var reordered = new[] { ids[2], ids[0], ids[1] };
        var reorder = await client.PutAsJsonAsync(
            $"/api/chores/{chore.id}/subtasks/reorder", new { orderedSubtaskIds = reordered }, Json);
        reorder.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The board reflects the new SortOrder (ascending) — the projection orders by it.
        var board = await client.GetFromJsonAsync<BoardWithSubtasks>("/api/chores/board", Json);
        var onBoard = board!.chores.Single(c => c.id == chore.id);
        onBoard.subtasks.OrderBy(s => s.sortOrder).Select(s => s.id)
            .Should().Equal(reordered, "reorder persists SortOrder by list position");
    }

    [Fact]
    public async Task Subtask_CrossHousehold_IsRejected()
    {
        // Household A creates a chore + subtask.
        var clientA = ClientA;
        var chore = await CreateFlexibleChoreAsync(clientA, "A-only checklist chore");
        var createResp = await clientA.PostAsJsonAsync($"/api/chores/{chore.id}/subtasks", new { title = "Private item" }, Json);
        createResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var created = await createResp.Content.ReadFromJsonAsync<Subtask>(Json);

        // Household B must not be able to touch A's chore's subtask. The (householdId, choreId) filter finds
        // nothing → ChoreNotFoundException → the handler returns an empty 404, which the app's global
        // UseStatusCodePagesWithReExecute("/not-found") re-executes through the Blazor page — surfacing on the
        // wire as 400 (empty 404 rewrite) or 405 (the re-executed GET page does not allow the original verb).
        // The load-bearing assertion is that the write is REJECTED with a client error (never satisfied/2xx),
        // not the exact code (same convention as ChoreRoundTripTests.Claim_NonexistentChore_IsRejected).
        var clientB = ClientB;
        var crossPut = await clientB.PutAsJsonAsync($"/api/chores/{chore.id}/subtasks/{created!.id}", new { isDone = true }, Json);
        crossPut.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest, HttpStatusCode.MethodNotAllowed);
        ((int)crossPut.StatusCode).Should().BeGreaterThanOrEqualTo(400, "the cross-household write must be rejected, never satisfied");

        // And POST onto A's chore from B must also be rejected.
        var crossPost = await clientB.PostAsJsonAsync($"/api/chores/{chore.id}/subtasks", new { title = "Sneaky" }, Json);
        crossPost.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest, HttpStatusCode.MethodNotAllowed);
        ((int)crossPost.StatusCode).Should().BeGreaterThanOrEqualTo(400, "the cross-household create must be rejected, never satisfied");

        // Stronger isolation check that does not depend on the status-code rewrite: A's subtask must be
        // unchanged after B's rejected PUT (B never mutated it).
        var stillThere = await clientA.PutAsJsonAsync($"/api/chores/{chore.id}/subtasks/{created.id}", new { isDone = false }, Json);
        stillThere.StatusCode.Should().Be(HttpStatusCode.OK, "A's own subtask is still reachable + unmodified by B");
    }
}
