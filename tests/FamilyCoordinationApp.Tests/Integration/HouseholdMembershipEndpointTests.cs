using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Tenancy;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// Every command that takes a user id checks it through the one membership validator (quest 82fc7f54).
/// <list type="bullet">
/// <item><b>Foreign user:</b> household A's caller naming household B's user is a 400 with a message on every
/// user-id input: assignee, owner, roster seed, hand-off target, completion participants, roster assign and roster
/// leave.</item>
/// <item><b>Disabled member</b> (<c>IsWhitelisted = false</c>): can't be NEWLY assigned (assignee, owner, roster seed,
/// hand-off, roster assign), but an existing reference is not rewritten: re-saving a chore whose owner is now disabled
/// still succeeds, and a disabled member can still be removed from a roster or credited on a completion.</item>
/// </list>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class HouseholdMembershipEndpointTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const int Alice = ChoresWebAppFactory.UserAId;
    private const int Amy = ChoresWebAppFactory.UserA2Id;
    private const int ForeignBob = ChoresWebAppFactory.UserBId;

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private sealed record ChoreWire(int id, uint version, int? ownerUserId, int? assigneeUserId);
    private sealed record ErrorBody(string message);

    private HttpClient AliceClient() => _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

    private static HttpContent Body(object body) => JsonContent.Create(body, options: Json);

    private IDbContextFactory<ApplicationDbContext> DbFactory =>
        _factory.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

    private async Task DisableAmyAsync()
    {
        using var _ = _factory.Services.GetRequiredService<ITenantContext>().RunAs(ChoresWebAppFactory.HouseholdAId);
        await using var db = await DbFactory.CreateDbContextAsync();
        var amy = await db.Users.SingleAsync(u => u.Id == Amy);
        amy.IsWhitelisted = false;
        await db.SaveChangesAsync();
    }

    private static async Task ShouldBeRefusedAsync(HttpResponseMessage resp, string because)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because);
        var body = await resp.Content.ReadFromJsonAsync<ErrorBody>(Json);
        body!.message.Should().NotBeNullOrWhiteSpace();
    }

    private static object NewChore(string name, int? ownerUserId = null, int? assigneeUserId = null,
        int requiredCount = 1, int[]? assignedUserIds = null) => new
    {
        name,
        recurrenceMode = "flexible",
        intervalDays = 7,
        effortTier = "standard",
        ownerUserId,
        assigneeUserId,
        requiredCount,
        assignedUserIds,
    };

    private async Task<ChoreWire> CreateAsync(HttpClient client, object body)
    {
        var resp = await client.PostAsync("/api/chores/", Body(body));
        resp.StatusCode.Should().Be(HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<ChoreWire>(Json))!;
    }

    private async Task<int> CountChoresNamedAsync(string name)
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        return await db.Chores.CountAsync(c => c.Name == name);
    }

    // ── A foreign household's user is refused on every user-id input ────────────────────────────────

    [Fact]
    public async Task Create_refuses_a_foreign_assignee_owner_or_roster_member()
    {
        using var client = AliceClient();

        await ShouldBeRefusedAsync(await client.PostAsync("/api/chores/", Body(NewChore("f-assignee", assigneeUserId: ForeignBob))), "assignee");
        await ShouldBeRefusedAsync(await client.PostAsync("/api/chores/", Body(NewChore("f-owner", ownerUserId: ForeignBob))), "owner");
        await ShouldBeRefusedAsync(await client.PostAsync("/api/chores/",
            Body(NewChore("f-roster", requiredCount: 2, assignedUserIds: [Alice, ForeignBob]))), "roster seed");

        foreach (var name in new[] { "f-assignee", "f-owner", "f-roster" })
        {
            (await CountChoresNamedAsync(name)).Should().Be(0, "a refused create stores nothing");
        }
    }

    [Fact]
    public async Task Update_refuses_a_foreign_owner()
    {
        using var client = AliceClient();
        var chore = await CreateAsync(client, NewChore("u-owner"));

        var resp = await client.PutAsync($"/api/chores/{chore.id}", Body(new
        {
            name = "u-owner", recurrenceMode = "flexible", intervalDays = 7, effortTier = "standard",
            version = chore.version, ownerUserId = ForeignBob,
        }));

        await ShouldBeRefusedAsync(resp, "owner on update");
    }

    [Fact]
    public async Task Handoff_and_complete_refuse_a_foreign_user()
    {
        using var client = AliceClient();
        var chore = await CreateAsync(client, NewChore("h-foreign"));

        await ShouldBeRefusedAsync(await client.PostAsync($"/api/chores/{chore.id}/handoff",
            Body(new { targetUserId = ForeignBob, version = chore.version })), "hand-off target");
        await ShouldBeRefusedAsync(await client.PostAsync($"/api/chores/{chore.id}/complete",
            Body(new { version = chore.version, participantUserIds = new[] { ForeignBob } })), "completion participant");
    }

    [Fact]
    public async Task Roster_assign_and_leave_refuse_a_foreign_user()
    {
        using var client = AliceClient();
        var chore = await CreateAsync(client, NewChore("r-foreign", requiredCount: 2));

        await ShouldBeRefusedAsync(await client.PostAsync($"/api/chores/{chore.id}/roster/assign",
            Body(new { subjectUserId = ForeignBob, version = chore.version })), "roster assign subject");
        await ShouldBeRefusedAsync(await client.PostAsync($"/api/chores/{chore.id}/roster/leave",
            Body(new { subjectUserId = ForeignBob, version = chore.version })), "roster leave subject");
    }

    // ── A disabled member can't be newly assigned ───────────────────────────────────────────────────

    [Fact]
    public async Task Create_refuses_a_disabled_assignee_owner_or_roster_member()
    {
        await DisableAmyAsync();
        using var client = AliceClient();

        await ShouldBeRefusedAsync(await client.PostAsync("/api/chores/", Body(NewChore("d-assignee", assigneeUserId: Amy))), "assignee");
        await ShouldBeRefusedAsync(await client.PostAsync("/api/chores/", Body(NewChore("d-owner", ownerUserId: Amy))), "owner");
        await ShouldBeRefusedAsync(await client.PostAsync("/api/chores/",
            Body(NewChore("d-roster", requiredCount: 2, assignedUserIds: [Alice, Amy]))), "roster seed");
    }

    [Fact]
    public async Task Handoff_and_roster_assign_refuse_a_disabled_member()
    {
        using var client = AliceClient();
        var single = await CreateAsync(client, NewChore("d-handoff"));
        var multi = await CreateAsync(client, NewChore("d-roster-assign", requiredCount: 2));
        await DisableAmyAsync();

        await ShouldBeRefusedAsync(await client.PostAsync($"/api/chores/{single.id}/handoff",
            Body(new { targetUserId = Amy, version = single.version })), "hand-off target");
        await ShouldBeRefusedAsync(await client.PostAsync($"/api/chores/{multi.id}/roster/assign",
            Body(new { subjectUserId = Amy, version = multi.version })), "roster assign subject");
    }

    [Fact]
    public async Task Update_refuses_making_a_disabled_member_the_owner()
    {
        using var client = AliceClient();
        var chore = await CreateAsync(client, NewChore("d-owner-change"));
        await DisableAmyAsync();

        var resp = await client.PutAsync($"/api/chores/{chore.id}", Body(new
        {
            name = "d-owner-change", recurrenceMode = "flexible", intervalDays = 7, effortTier = "standard",
            version = chore.version, ownerUserId = Amy,
        }));

        await ShouldBeRefusedAsync(resp, "a disabled member can't become the owner");
    }

    // ── An existing reference to a disabled member is not rewritten ─────────────────────────────────

    [Fact]
    public async Task Update_keeps_an_owner_who_has_since_been_disabled()
    {
        // The edit sheet re-sends the chore's current ownerUserId on every save (EditChoreSheet.svelte), so a
        // disabled owner must not make the chore uneditable.
        using var client = AliceClient();
        var chore = await CreateAsync(client, NewChore("d-owner-kept", ownerUserId: Amy));
        await DisableAmyAsync();

        var resp = await client.PutAsync($"/api/chores/{chore.id}", Body(new
        {
            name = "d-owner-kept renamed", recurrenceMode = "flexible", intervalDays = 7, effortTier = "standard",
            version = chore.version, ownerUserId = Amy,
        }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await resp.Content.ReadFromJsonAsync<ChoreWire>(Json))!.ownerUserId.Should().Be(Amy);
    }

    [Fact]
    public async Task A_disabled_member_can_still_be_removed_from_a_roster()
    {
        using var client = AliceClient();
        var chore = await CreateAsync(client, NewChore("d-roster-leave", requiredCount: 2, assignedUserIds: [Alice, Amy]));
        await DisableAmyAsync();

        var resp = await client.PostAsync($"/api/chores/{chore.id}/roster/leave",
            Body(new { subjectUserId = Amy, version = chore.version }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_disabled_member_can_still_be_credited_on_a_completion()
    {
        using var client = AliceClient();
        var chore = await CreateAsync(client, NewChore("d-complete"));
        await DisableAmyAsync();

        var resp = await client.PostAsync($"/api/chores/{chore.id}/complete",
            Body(new { version = chore.version, participantUserIds = new[] { Amy } }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_active_member_is_still_assignable_everywhere()
    {
        using var client = AliceClient();
        var chore = await CreateAsync(client, NewChore("a-active", ownerUserId: Amy, assigneeUserId: Amy));
        chore.ownerUserId.Should().Be(Amy);
        chore.assigneeUserId.Should().Be(Amy);

        var resp = await client.PostAsync($"/api/chores/{chore.id}/handoff",
            Body(new { targetUserId = Alice, version = chore.version }));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
    }
}
