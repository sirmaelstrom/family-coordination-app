using System.Net;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Integration;

[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class UnseededCalendarFeedEndpointTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task FeedPath_SkipsSetupRedirect_WhenNoHouseholdExists()
    {
        using var factory = new ChoresWebAppFactory(postgres);
        await factory.EnsureUnseededAsync();

        var response = await factory.CreateAnonymousClient().GetAsync("/api/calendar/meal-plan.ics?token=bogus");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.Location.Should().BeNull();
    }
}
