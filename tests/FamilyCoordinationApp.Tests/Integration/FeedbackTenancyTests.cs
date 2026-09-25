using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// Feedback authors across households under the read switch (fca-household-scope WP-02 step 7a; council round 1,
/// opus). <c>User</c> is a tenant entity, so without the Tenant bypass on <c>FeedbackService.GetFeedbackAsync</c>'s
/// <c>Include(f => f.User)</c>, the site admin (Alice, household A) would see household B's items with
/// <c>authorName = null, authorDeleted = true</c>. The existing V6 test asserts counts only; this asserts the author.
/// <para>Negative control: remove the bypass, and Bob's item comes back authorless.</para>
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
}
