using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// The production "DiscordWebhook" client does not follow redirects (quest e9c2e497): DiscordWebhookUrl only vouches for
/// the host it checked, so a 3xx from it must not carry the digest POST elsewhere. Read from the booted host's own
/// handler chain, so removing the registration's ConfigurePrimaryHttpMessageHandler turns this red (council round 1:
/// the sender tests replace the factory and could not see it).
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class DiscordWebhookClientTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public void The_production_webhook_client_does_not_follow_redirects()
    {
        var handlers = _factory.Services.GetRequiredService<IHttpMessageHandlerFactory>();
        HttpMessageHandler handler = handlers.CreateHandler("DiscordWebhook");
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        var primary = handler.Should().BeOfType<SocketsHttpHandler>().Subject;
        primary.AllowAutoRedirect.Should().BeFalse();
    }
}
