using System.Net;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Services.Digest;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// The digest webhook URL rule (quest e9c2e497): only a Discord incoming-webhook URL is accepted, so a household
/// member cannot point the server's POST at an internal host. Negative control: with <see cref="DiscordWebhookUrl.Problem"/>
/// returning null for everything, every Refused case is red.
/// </summary>
public class DiscordWebhookUrlTests
{
    [Theory]
    [InlineData("https://discord.com/api/webhooks/123/abc-DEF_ghi")]
    [InlineData("https://discordapp.com/api/webhooks/123/abc")]
    [InlineData("https://canary.discord.com/api/webhooks/123/abc")]
    [InlineData("https://DISCORD.com/api/webhooks/123/abc?wait=true")]
    public void Accepted(string url) => DiscordWebhookUrl.Problem(url).Should().BeNull();

    [Theory]
    [InlineData("http://discord.com/api/webhooks/123/abc")]          // not https
    [InlineData("https://localhost/api/webhooks/123/abc")]           // internal host
    [InlineData("https://127.0.0.1/api/webhooks/123/abc")]
    [InlineData("https://192.168.50.1/api/webhooks/123/abc")]
    [InlineData("https://familyapp-postgres/api/webhooks/1/a")]      // the docker network
    [InlineData("https://discord.com.evil.example/api/webhooks/1/a")] // suffix trick
    [InlineData("https://evil.example/discord.com/api/webhooks/1/a")]
    [InlineData("https://discord.com@evil.example/api/webhooks/1/a")] // userinfo: the host is evil.example
    [InlineData("https://user:pw@discord.com/api/webhooks/1/a")]
    [InlineData("https://discord.com:8443/api/webhooks/1/a")]        // explicit port
    [InlineData("https://discord.com/api/v9/users/@me")]             // not a webhook path
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    public void Refused(string url) => DiscordWebhookUrl.Problem(url).Should().NotBeNull();

    [Fact]
    public void A_URL_over_the_length_bound_is_refused_and_the_reason_never_echoes_it()
    {
        var url = "https://discord.com/api/webhooks/1/" + new string('x', DiscordWebhookUrl.MaxLength);
        var problem = DiscordWebhookUrl.Problem(url);

        problem.Should().NotBeNull();
        problem.Should().NotContain("xxxx", "the URL is a secret (MN7)");
    }

    /// <summary>
    /// Quest 9b750f8c: the longest accepted URL, once protected, fits the WebhookUrlProtected column. Measured with a
    /// real data protector: payload expansion does not depend on the purpose string.
    /// </summary>
    [Fact]
    public void The_longest_accepted_URL_fits_the_protected_column_once_protected()
    {
        var protector = new EphemeralDataProtectionProvider().CreateProtector("digest-webhook-test");
        var longest = "https://discord.com/api/webhooks/1/" + new string('x', DiscordWebhookUrl.MaxLength - 35);
        longest.Length.Should().Be(DiscordWebhookUrl.MaxLength);
        DiscordWebhookUrl.Problem(longest).Should().BeNull();

        protector.Protect(longest).Length.Should().BeLessThanOrEqualTo(FieldLengths.HouseholdChoreDigestSettings.WebhookUrlProtected);
    }

    [Fact]
    public async Task The_sender_refuses_an_internal_URL_before_any_request_leaves()
    {
        var handler = new CountingHandler();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("DiscordWebhook")).Returns(new HttpClient(handler));
        var sender = new DiscordWebhookDigestSender(factory.Object, NullLogger<DiscordWebhookDigestSender>.Instance);

        var act = () => sender.SendAsync("https://localhost:8080/api/webhooks/1/a",
            new DigestModel("headline", 0, 0, [], [], 0));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Refusing to send the digest*");
        handler.Calls.Should().Be(0);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
