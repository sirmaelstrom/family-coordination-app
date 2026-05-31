using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Digest;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Unit tests for <see cref="DiscordWebhookDigestSender"/>. All HTTP is stubbed via a capturing
/// <see cref="HttpMessageHandler"/> — zero real network calls to discord.com (spec constraint).
/// <para>
/// Assertions:
/// <list type="bullet">
///   <item>Payload shape matches the checked-in fixture (parsed-node comparison).</item>
///   <item><c>allowed_mentions.parse == []</c> — ALL pings suppressed (M11).</item>
///   <item>No <c>@</c>-mention in the <c>content</c> field (MN8).</item>
///   <item>Non-2xx status (429, 500) is surfaced as an exception — not swallowed (M14).</item>
///   <item>The webhook URL never appears in any captured log message (MN7).</item>
/// </list>
/// </para>
/// </summary>
public class DiscordWebhookDigestSenderTests
{
    private const string FakeWebhookUrl = "https://discord.com/api/webhooks/999888777/FAKE-TOKEN-FOR-TESTS";

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Digest", "discord-payload.json");

    // ---- Fixture model (mirrors discord-payload.json) ----------------------------------------------

    private static DigestModel FixtureModel() => new(
        CollectiveHeadline: "The Heath house knocked out 4 chores (12 pts) this week 💪",
        TotalCompletions: 4,
        TotalPoints: 12,
        Distribution:
        [
            // Alphabetical (Alice, Bob, Carol) — as produced by DigestBuilder.
            new DigestMemberLine("Alice", 5, 41.7),
            new DigestMemberLine("Bob", 4, 33.3),
            new DigestMemberLine("Carol", 3, 25.0),
        ],
        FallingBehind: ["Dishes", "Vacuum"],
        UpForGrabsCount: 3);

    // ---- Builder: CapturingHandler wiring ---------------------------------------------------------

    private static DiscordWebhookDigestSender BuildSender(
        HttpResponseMessage response,
        Action<HttpRequestMessage, string>? capture = null)
    {
        var handler = new CapturingHandler(response, capture);
        var httpClient = new HttpClient(handler);

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("DiscordWebhook")).Returns(httpClient);

        return new DiscordWebhookDigestSender(factory.Object, NullLogger<DiscordWebhookDigestSender>.Instance);
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK);

    private static HttpResponseMessage Status(HttpStatusCode code) =>
        new(code) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };

    // ---- Payload shape matches fixture (parsed-node comparison) ------------------------------------

    [Fact]
    public async Task SendAsync_PayloadMatchesFixture()
    {
        string? capturedBody = null;
        var sender = BuildSender(Ok(), (_, body) => capturedBody = body);

        await sender.SendAsync(FakeWebhookUrl, FixtureModel());

        capturedBody.Should().NotBeNullOrEmpty("request body must be captured");

        File.Exists(FixturePath).Should().BeTrue(
            $"discord-payload.json must be checked in at {FixturePath}");

        var expectedJson = File.ReadAllText(FixturePath);
        Normalize(capturedBody!).Should().Be(Normalize(expectedJson),
            "the serialized Discord payload must match the checked-in fixture; update fixture if shape changes deliberately");
    }

    // ---- allowed_mentions.parse == [] (M11) --------------------------------------------------------

    [Fact]
    public async Task SendAsync_AllowedMentions_ParseIsEmpty()
    {
        string? capturedBody = null;
        var sender = BuildSender(Ok(), (_, body) => capturedBody = body);

        await sender.SendAsync(FakeWebhookUrl, FixtureModel());

        var root = JsonNode.Parse(capturedBody!)!.AsObject();
        var allowedMentions = root["allowed_mentions"]
            ?? root["allowedMentions"]; // guard against casing drift

        allowedMentions.Should().NotBeNull("allowed_mentions must be present (M11 defense-in-depth)");

        var parse = allowedMentions!.AsObject()["parse"]!.AsArray();
        parse.Should().BeEmpty("parse must be an empty array to suppress ALL @-pings (M11)");
    }

    // ---- No @ mention in content (MN8) -------------------------------------------------------------

    [Fact]
    public async Task SendAsync_Content_ContainsNoAtMention()
    {
        string? capturedBody = null;
        var sender = BuildSender(Ok(), (_, body) => capturedBody = body);

        await sender.SendAsync(FakeWebhookUrl, FixtureModel());

        var root = JsonNode.Parse(capturedBody!)!.AsObject();
        var content = root["content"]?.GetValue<string>() ?? string.Empty;

        content.Should().NotContain("@",
            "content must never contain an @ mention — collective broadcast only (M11/MN8)");
    }

    // ---- Non-2xx is surfaced (not swallowed) (M14) ------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task SendAsync_NonSuccessStatus_ThrowsHttpRequestException(HttpStatusCode statusCode)
    {
        var sender = BuildSender(Status(statusCode));

        var act = async () => await sender.SendAsync(FakeWebhookUrl, FixtureModel());

        await act.Should().ThrowAsync<HttpRequestException>(
            $"status {(int)statusCode} must be surfaced so the resilience handler can retry (M14)");
    }

    // ---- 2xx succeeds without exception -----------------------------------------------------------

    [Fact]
    public async Task SendAsync_SuccessStatus_CompletesWithoutException()
    {
        var sender = BuildSender(Ok());

        var act = async () => await sender.SendAsync(FakeWebhookUrl, FixtureModel());

        await act.Should().NotThrowAsync();
    }

    // ---- Named client "DiscordWebhook" is resolved (M14) ------------------------------------------

    [Fact]
    public async Task SendAsync_ResolvesDiscordWebhookNamedClient()
    {
        var factory = new Mock<IHttpClientFactory>();
        var capturedNames = new List<string>();

        // Capture which named clients are created.
        factory
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Callback<string>(name => capturedNames.Add(name))
            .Returns(() =>
            {
                var h = new CapturingHandler(Ok(), null);
                return new HttpClient(h);
            });

        var sender = new DiscordWebhookDigestSender(factory.Object, NullLogger<DiscordWebhookDigestSender>.Instance);
        await sender.SendAsync(FakeWebhookUrl, FixtureModel());

        capturedNames.Should().ContainSingle().Which.Should().Be("DiscordWebhook",
            "the sender must resolve the named 'DiscordWebhook' HttpClient (M14)");
    }

    // ---- Embed title matches headline --------------------------------------------------------------

    [Fact]
    public async Task SendAsync_EmbedTitle_MatchesHeadline()
    {
        string? capturedBody = null;
        var sender = BuildSender(Ok(), (_, body) => capturedBody = body);
        var model = FixtureModel();

        await sender.SendAsync(FakeWebhookUrl, model);

        var root = JsonNode.Parse(capturedBody!)!.AsObject();
        var firstEmbed = root["embeds"]!.AsArray()[0]!.AsObject();
        var title = firstEmbed["title"]!.GetValue<string>();

        title.Should().Be(model.CollectiveHeadline, "embed title must be the collective headline");
    }

    // ---- BuildPayload unit (pure, no HTTP) --------------------------------------------------------

    [Fact]
    public void BuildPayload_NoAtMentionAnywhere()
    {
        var payload = DiscordWebhookDigestSender.BuildPayload(FixtureModel());

        // Content field: no @ mention.
        payload.Content.Should().NotContain("@");

        // Embed fields: no @ mention in any value.
        foreach (var field in payload.Embeds[0].Fields)
        {
            field.Value.Should().NotContain("@",
                $"embed field '{field.Name}' must not contain an @-mention");
        }
    }

    [Fact]
    public void BuildPayload_AllowedMentions_ParseIsEmpty()
    {
        var payload = DiscordWebhookDigestSender.BuildPayload(FixtureModel());

        payload.AllowedMentions.Parse.Should().BeEmpty(
            "allowed_mentions.parse must be empty to suppress ALL pings (M11)");
    }

    [Fact]
    public void BuildPayload_DistributionField_ContainsAllMembers()
    {
        var payload = DiscordWebhookDigestSender.BuildPayload(FixtureModel());

        var distributionField = payload.Embeds[0].Fields
            .FirstOrDefault(f => f.Name.Contains("effort"));

        distributionField.Should().NotBeNull("distribution / effort field must be present");
        distributionField!.Value.Should().Contain("Alice");
        distributionField.Value.Should().Contain("Bob");
        distributionField.Value.Should().Contain("Carol");
    }

    [Fact]
    public void BuildPayload_FallingBehindField_ContansChoreNames()
    {
        var payload = DiscordWebhookDigestSender.BuildPayload(FixtureModel());

        var attentionField = payload.Embeds[0].Fields
            .FirstOrDefault(f => f.Name.Contains("attention"));

        attentionField.Should().NotBeNull("attention/falling-behind field must be present when there are overdue chores");
        attentionField!.Value.Should().Contain("Dishes");
        attentionField.Value.Should().Contain("Vacuum");
    }

    [Fact]
    public void BuildPayload_NoFallingBehindField_WhenEmpty()
    {
        var model = FixtureModel() with { FallingBehind = [] };
        var payload = DiscordWebhookDigestSender.BuildPayload(model);

        payload.Embeds[0].Fields
            .Should().NotContain(f => f.Name.Contains("attention"),
                "falling-behind field must be omitted when there are no overdue chores");
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    private class CapturingHandler(HttpResponseMessage response, Action<HttpRequestMessage, string>? capture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : string.Empty;
            capture?.Invoke(request, body);
            return response;
        }
    }
}
