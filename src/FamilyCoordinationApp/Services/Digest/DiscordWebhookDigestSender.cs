using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FamilyCoordinationApp.Services.Digest;

/// <summary>
/// Sends a household <see cref="DigestModel"/> to a Discord channel via an incoming webhook.
/// <para>
/// Resolves the named <c>"DiscordWebhook"</c> <see cref="HttpClient"/> from
/// <see cref="IHttpClientFactory"/> (M14). The named client is registered with
/// <c>AddStandardResilienceHandler</c> in WP-06 so transient failures are retried transparently.
/// </para>
/// <para>
/// Security constraints (M11/MN7):
/// <list type="bullet">
///   <item>The webhook URL is NEVER logged (not even at debug level).</item>
///   <item>The payload always sets <c>allowed_mentions = { "parse": [] }</c> to suppress ALL
///   pings as defense-in-depth (M11).</item>
///   <item>The embed content never contains an <c>@</c>-prefixed mention (MN8).</item>
/// </list>
/// </para>
/// </summary>
public class DiscordWebhookDigestSender(
    IHttpClientFactory httpFactory,
    ILogger<DiscordWebhookDigestSender> logger) : IDigestSender
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <inheritdoc/>
    public async Task SendAsync(string webhookUrl, DigestModel model, CancellationToken ct = default)
    {
        var payload = BuildPayload(model);
        var client = httpFactory.CreateClient("DiscordWebhook");

        // NEVER log webhookUrl — use a fixed placeholder in any log message (MN7).
        logger.LogInformation("Sending weekly digest to Discord webhook (household: {Headline})", model.CollectiveHeadline);

        var response = await client.PostAsJsonAsync(webhookUrl, payload, SerializerOptions, ct);

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            logger.LogWarning("Discord webhook returned non-2xx status {StatusCode} — surfacing for resilience retry", status);

            // Surface non-2xx so the caller's resilience handler can retry (M14) and the
            // orchestrator can isolate this household's failure (M10).
            response.EnsureSuccessStatusCode();
        }
    }

    // ---- Payload builder (internal for testability) ------------------------------------------------

    internal static DiscordWebhookPayload BuildPayload(DigestModel model)
    {
        var fields = new List<DiscordEmbedField>();

        // Distribution field — neutral framing, never "leaderboard" or "ranking" copy.
        if (model.Distribution.Count > 0)
        {
            var lines = model.Distribution
                .Select(m => $"**{m.DisplayName}** — {m.Points} pt{(m.Points == 1 ? "" : "s")} ({m.SharePct:F1}%)");
            fields.Add(new DiscordEmbedField(
                Name: "📊 This week's effort",
                Value: string.Join("\n", lines),
                Inline: false));
        }

        // Falling-behind field — chore names, not people; framed as "needs attention".
        if (model.FallingBehind.Count > 0)
        {
            var lines = model.FallingBehind.Select(n => $"• {n}");
            fields.Add(new DiscordEmbedField(
                Name: "⏰ Needs attention",
                Value: string.Join("\n", lines),
                Inline: false));
        }

        // Up-for-grabs field — only show when there are open chores.
        if (model.UpForGrabsCount > 0)
        {
            fields.Add(new DiscordEmbedField(
                Name: "🙋 Up for grabs",
                Value: $"{model.UpForGrabsCount} chore{(model.UpForGrabsCount == 1 ? "" : "s")} open for anyone",
                Inline: false));
        }

        var embed = new DiscordEmbed(
            Title: model.CollectiveHeadline,
            Color: 0x5865F2,  // Discord blurple — neutral, not punitive red
            Fields: fields);

        return new DiscordWebhookPayload(
            // Empty content — all substance is in the embed.
            // No @ mentions anywhere (M11/MN8).
            Content: string.Empty,
            Embeds: [embed],
            // Defense-in-depth: suppress ALL role/user/everyone pings even if a chore name
            // somehow contained an @-sequence (M11).
            AllowedMentions: new DiscordAllowedMentions(Parse: []));
    }

    // ---- Payload shape (Discord Webhook Execute API) -----------------------------------------------

    internal record DiscordWebhookPayload(
        string Content,
        IReadOnlyList<DiscordEmbed> Embeds,
        [property: JsonPropertyName("allowed_mentions")] DiscordAllowedMentions AllowedMentions);

    internal record DiscordEmbed(
        string Title,
        int Color,
        IReadOnlyList<DiscordEmbedField> Fields);

    internal record DiscordEmbedField(
        string Name,
        string Value,
        bool Inline);

    internal record DiscordAllowedMentions(
        [property: JsonPropertyName("parse")] IReadOnlyList<string> Parse);
}
