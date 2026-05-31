using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services.Interfaces;

/// <summary>
/// Safe read-only view of a household's digest settings.
/// Never contains the plaintext or ciphertext webhook URL (MN7).
/// </summary>
public record DigestSettingsView(
    bool Enabled,
    DigestCadence Cadence,
    DayOfWeek SendDayOfWeek,
    int SendHourLocal,
    /// <summary>True when a webhook URL is stored (encrypted). Never the URL itself.</summary>
    bool HasWebhook,
    /// <summary>Masked hint shown in the UI (last 4 chars of the URL), or null when no webhook is set.</summary>
    string? WebhookHint,
    DateTime? LastSentAt);

/// <summary>
/// Mutation payload for digest settings.
/// Webhook field is tri-state: <see cref="WebhookProvided"/> distinguishes "caller omitted it" from "caller
/// explicitly passed null/empty" so the service can implement the frozen contract:
/// <list type="bullet">
///   <item><description><c>WebhookProvided = false</c> — leave <c>WebhookUrlProtected</c> unchanged.</description></item>
///   <item><description><c>WebhookProvided = true, WebhookUrl = non-blank string</c> — encrypt and store.</description></item>
///   <item><description><c>WebhookProvided = true, WebhookUrl = null or ""</c> — clear (set to null).</description></item>
/// </list>
/// </summary>
public record DigestSettingsUpdate(
    bool Enabled,
    DigestCadence Cadence,
    DayOfWeek SendDayOfWeek,
    int SendHourLocal,
    /// <summary>Whether the caller supplied a webhook value (true = update; false = leave unchanged).</summary>
    bool WebhookProvided = false,
    /// <summary>New webhook URL (plaintext). Only meaningful when <see cref="WebhookProvided"/> is true.</summary>
    string? WebhookUrl = null);

/// <summary>
/// Reads and writes per-household digest settings, encrypting the webhook URL at rest (M8).
/// Never logs or returns the plaintext or ciphertext webhook URL (MN7).
/// </summary>
public interface IDigestSettingsService
{
    /// <summary>
    /// Returns a safe view of the household's digest settings. If no row exists, returns defaults.
    /// The view never contains the webhook URL or ciphertext — only <c>HasWebhook</c> and a masked hint.
    /// </summary>
    Task<DigestSettingsView> GetAsync(int householdId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts the household's digest settings. Validates <c>SendHourLocal</c>, <c>SendDayOfWeek</c>, and
    /// <c>Cadence</c> — throws <see cref="DigestSettingsValidationException"/> on invalid input (→ 400 at the
    /// endpoint). The webhook tri-state on <paramref name="update"/> controls whether the stored ciphertext
    /// is replaced, cleared, or left untouched.
    /// </summary>
    /// <exception cref="DigestSettingsValidationException">
    /// Thrown when <c>SendHourLocal</c> is outside [0, 23], <c>SendDayOfWeek</c> is not a valid enum value,
    /// or <c>Cadence</c> is not a valid enum value.
    /// </exception>
    Task UpdateAsync(int householdId, DigestSettingsUpdate update, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decrypts and returns the webhook URL for the digest sender (WP-05 only).
    /// Returns <c>null</c> when no webhook is configured or when decryption fails (e.g. key rotation —
    /// the household is treated as unconfigured for that run).
    /// <para>
    /// SEND-ONLY: this method must never be called from any cookie-authenticated read path that returns
    /// data to a client. Only <c>DigestService</c> calls it at send time.
    /// </para>
    /// </summary>
    Task<string?> GetDecryptedWebhookAsync(int householdId, CancellationToken cancellationToken = default);
}
