namespace FamilyCoordinationApp.Services.Digest;

/// <summary>
/// Delivers a household <see cref="DigestModel"/> to its configured channel.
/// The caller (WP-05 <c>DigestService</c>) holds the decrypted webhook URL; the sender
/// never persists or logs it (MN7).
/// </summary>
public interface IDigestSender
{
    /// <summary>
    /// Send the digest. Throws on delivery failure so the resilience handler (M14) or the
    /// orchestrator can retry / isolate the failure.
    /// </summary>
    /// <param name="webhookUrl">Plaintext webhook URL — must never be logged.</param>
    /// <param name="model">The assembled digest model.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SendAsync(string webhookUrl, DigestModel model, CancellationToken ct = default);
}
