namespace FamilyCoordinationApp.Services.Digest;

/// <summary>
/// Outcome counts of a single <see cref="IDigestService.RunDueAsync"/> invocation. Counts only —
/// never any webhook URL or household-identifying payload (MN7).
/// </summary>
/// <param name="Sent">Households whose digest was successfully claimed, built, and handed to the sender.</param>
/// <param name="Skipped">
/// Households that were <b>enabled AND webhook-configured</b> but were <b>not due</b> (wrong local
/// weekday/hour, or already sent for this window). Disabled and unconfigured households are filtered out of
/// the candidate query entirely and are <b>NOT</b> counted here.
/// </param>
/// <param name="Failed">
/// Households that were claimed but could not be delivered: the webhook decryption returned null (cleared /
/// key-rotated between select and claim) or the sender threw. In both cases the claim is compensated (the
/// prior <c>LastSentAt</c> is restored) so a later hourly tick retries the window (M10 failure isolation).
/// </param>
public record DigestRunSummary(int Sent, int Skipped, int Failed);

/// <summary>
/// Orchestrates the weekly chore equity digest: finds <b>due</b> households, atomically claims each one's
/// send window, builds the digest from that household's own data (M1 isolation), decrypts the webhook, sends,
/// and stamps <c>LastSentAt</c> — idempotent and failure-isolated (M10).
/// <para>
/// NOT a <c>BackgroundService</c> / <c>IHostedService</c> (MN1) — this is a plain scoped service invoked by
/// the token-gated WP-06 endpoint, which an external cron fires. Firing is the cron→endpoint, never an
/// in-process timer.
/// </para>
/// </summary>
public interface IDigestService
{
    /// <summary>
    /// Run the digest for every household that is due as of <paramref name="now"/>. Each household is
    /// processed independently and failure-isolated; one bad household never aborts the run (M10).
    /// </summary>
    /// <param name="now">
    /// The UTC instant to evaluate dueness against. Defaults to the injected <c>TimeProvider</c>'s UTC now
    /// when null (never <see cref="DateTime.UtcNow"/>).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<DigestRunSummary> RunDueAsync(DateTime? now = null, CancellationToken ct = default);
}
