using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyCoordinationApp.Data.Entities;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// Feedback authors across households under the read switch (fca-household-scope WP-02 step 7a; council round 1,
/// opus). <c>User</c> is a tenant entity, so without the Tenant bypass on <c>FeedbackService.GetFeedbackAsync</c>'s
/// <c>Include(f => f.User)</c>, the site admin (Alice, household A) would see household B's items with
/// <c>authorName = null, authorDeleted = true</c>. The existing V6 test asserts counts only; this asserts the author.
/// <para>Negative control: remove the bypass, and Bob's item comes back authorless.</para>
/// <para>The bypass is the site admin's alone (PR #121 review 1, opus): a non-admin's read keeps its <c>User</c>
/// include under the Tenant filter. Negative control: put the bypass back on the base query, and the non-admin sees
/// another household's author.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class FeedbackTenancyTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private sealed record FeedbackWire(int id, string message, string? authorName, bool authorDeleted);
    private sealed record FeedbackListWire(bool isSiteAdmin, List<FeedbackWire> items);

    [Fact]
    public async Task The_site_admin_sees_another_households_author()
    {
        var submit = await _factory.CreateClientAs(ChoresWebAppFactory.UserBEmail).PostAsJsonAsync(
            "/api/settings/feedback/", new { type = "bug", message = "from household B" }, Json);
        submit.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = (await _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail)
            .GetFromJsonAsync<FeedbackListWire>("/api/settings/feedback/", Json))!;

        list.isSiteAdmin.Should().BeTrue();
        var item = list.items.Should().ContainSingle(i => i.message == "from household B").Subject;
        item.authorName.Should().Be("Bob B", "the admin's list must load household B's author past the Tenant filter");
        item.authorDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task A_non_admins_feedback_read_keeps_the_author_include_under_the_Tenant_filter()
    {
        // A deliberate invariant break, written through an Unfiltered context: household B's row with household A's
        // Alice as its author. Only the Tenant filter on the User include keeps Alice out of Bob's list.
        await using (var db = await new PostgresDbContextFactory(_factory.ConnectionString).CreateDbContextAsync())
        {
            db.Feedbacks.Add(new Feedback
            {
                UserId = ChoresWebAppFactory.UserAId,
                HouseholdId = ChoresWebAppFactory.HouseholdBId,
                Type = FeedbackType.Bug,
                Message = "household B's row, household A's author",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var list = (await _factory.CreateClientAs(ChoresWebAppFactory.UserBEmail)
            .GetFromJsonAsync<FeedbackListWire>("/api/settings/feedback/", Json))!;

        list.isSiteAdmin.Should().BeFalse();
        var item = list.items.Should().ContainSingle(i => i.message == "household B's row, household A's author").Subject;
        item.authorName.Should().BeNull("a non-admin's read goes through the Tenant filter, so household A's user isn't loaded");
        item.authorDeleted.Should().BeTrue();
    }
}
