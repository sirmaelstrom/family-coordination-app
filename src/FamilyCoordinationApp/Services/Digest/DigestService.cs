using Microsoft.EntityFrameworkCore;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Dtos;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Services.Digest;

/// <summary>
/// Orchestrates the weekly chore equity digest (WP-05). Scoped service invoked by the WP-06 token-gated
/// endpoint (cron→endpoint, not a <c>BackgroundService</c> — MN1). Each household is processed with a
/// short-lived <see cref="ApplicationDbContext"/> per op (M2) and is fully scoped to its own
/// <c>HouseholdId</c> (M1 — the id only ever comes from the settings rows, never a client).
/// <para>
/// Idempotency + concurrency (council C2): the send window is claimed with a single atomic conditional
/// <c>ExecuteUpdateAsync</c> that stamps <c>LastSentAt</c>; only the run whose UPDATE matched exactly one
/// row proceeds to build+send. A second concurrent cron hit matches zero rows and skips — no double-post.
/// </para>
/// <para>
/// Failure isolation (M10): build/decrypt/send for one household runs in try/catch; on a null webhook or a
/// sender exception the claim is <b>compensated</b> (the prior <c>LastSentAt</c> is restored) so a later
/// hourly tick retries the window, the household is counted <c>Failed</c>, and the run continues. The
/// webhook plaintext is never logged or returned (MN7).
/// </para>
/// </summary>
public class DigestService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ChoreEquityCalculator equity,
    ChoreStatusCalculator status,
    DigestBuilder builder,
    IDigestSender sender,
    IDigestSettingsService settings,
    TimeZoneInfo tz,
    TimeProvider timeProvider,
    ILogger<DigestService> logger) : IDigestService
{
    /// <inheritdoc />
    public async Task<DigestRunSummary> RunDueAsync(DateTime? now = null, CancellationToken ct = default)
    {
        var asOf = now ?? timeProvider.GetUtcNow().UtcDateTime;
        asOf = DateTime.SpecifyKind(asOf, DateTimeKind.Utc);

        // Candidate set: enabled + webhook-configured only (M1 — HouseholdId comes from these rows, never a
        // client). Disabled/unconfigured rows are filtered out here and are NOT counted in Skipped.
        List<DigestCandidate> candidates;
        await using (var context = await dbFactory.CreateDbContextAsync(ct))
        {
            candidates = await context.ChoreDigestSettings
                .Where(s => s.Enabled && s.WebhookUrlProtected != null)
                .Select(s => new DigestCandidate(s.HouseholdId, s.SendDayOfWeek, s.SendHourLocal, s.LastSentAt))
                .ToListAsync(ct);
        }

        int sent = 0, skipped = 0, failed = 0;

        foreach (var candidate in candidates)
        {
            // Wrap each household so even an unexpected throw is isolated (M10) and never aborts the run.
            try
            {
                var outcome = await ProcessHouseholdAsync(candidate, asOf, ct);
                switch (outcome)
                {
                    case ProcessOutcome.Sent: sent++; break;
                    case ProcessOutcome.Skipped: skipped++; break;
                    case ProcessOutcome.Failed: failed++; break;
                }
            }
            catch (Exception ex)
            {
                // No URL, no payload — only the household id (M1-scoped, MN7).
                logger.LogError(ex, "Digest run failed unexpectedly for household {HouseholdId}", candidate.HouseholdId);
                failed++;
            }
        }

        logger.LogInformation("Digest run complete: Sent={Sent} Skipped={Skipped} Failed={Failed}", sent, skipped, failed);
        return new DigestRunSummary(sent, skipped, failed);
    }

    private async Task<ProcessOutcome> ProcessHouseholdAsync(DigestCandidate candidate, DateTime now, CancellationToken ct)
    {
        var h = candidate.HouseholdId;

        // Not the right local weekday/hour, or already sent this window => skipped (E10).
        if (!DigestDue.IsDue(candidate.SendDayOfWeek, candidate.SendHourLocal, candidate.LastSentAt, now, tz))
        {
            return ProcessOutcome.Skipped;
        }

        // Capture the prior LastSentAt so a failed send can be compensated back to it.
        var priorLastSentAt = candidate.LastSentAt;
        var windowStart = DigestDue.SendWindowStartUtc(candidate.SendHourLocal, now, tz);

        // ATOMIC CLAIM (council C2): a single conditional UPDATE at the DB. Only the run whose UPDATE matched
        // exactly one row proceeds — a concurrent cron hit matches zero and skips. ExecuteUpdateAsync is a
        // direct atomic SQL UPDATE (EF 10) with no change-tracker race.
        await using (var claimContext = await dbFactory.CreateDbContextAsync(ct))
        {
            var claimed = await claimContext.ChoreDigestSettings
                .Where(s => s.HouseholdId == h && s.Enabled && s.WebhookUrlProtected != null
                            && (s.LastSentAt == null || s.LastSentAt < windowStart))
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastSentAt, now), ct);

            if (claimed != 1)
            {
                // Another run already claimed this household's window — not due to us.
                return ProcessOutcome.Skipped;
            }
        }

        // The claimer only: build the digest from this household's own data (M1). If anything below fails,
        // compensate (restore priorLastSentAt) so a later tick retries.
        try
        {
            var model = await BuildModelAsync(h, now, ct);

            var url = await settings.GetDecryptedWebhookAsync(h, ct);
            if (url is null)
            {
                // Webhook cleared / key-rotated between select and claim — treat as unconfigured for this run.
                await CompensateAsync(h, priorLastSentAt, ct);
                logger.LogWarning("Digest skipped for household {HouseholdId}: webhook unavailable (decrypt failed or cleared)", h);
                return ProcessOutcome.Failed;
            }

            await sender.SendAsync(url, model, ct);
            logger.LogInformation("Digest sent for household {HouseholdId}", h);
            return ProcessOutcome.Sent;
        }
        catch (Exception ex)
        {
            // Compensate so the hourly tick retries this window; isolate the failure (M10). Never log the URL.
            await CompensateAsync(h, priorLastSentAt, ct);
            logger.LogError(ex, "Digest send failed for household {HouseholdId}; claim rolled back for retry", h);
            return ProcessOutcome.Failed;
        }
    }

    /// <summary>
    /// Assemble the household's digest model from its own members, this-week completions, and active chores
    /// (M1 — every query is scoped to <paramref name="householdId"/>). Mirrors the read/projection shape of
    /// <c>ChoreBoardService</c> so dueness/staleness are computed exactly as the board/lens compute them.
    /// </summary>
    private async Task<DigestModel> BuildModelAsync(int householdId, DateTime now, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);

        var householdName = await context.Households
            .Where(hh => hh.Id == householdId)
            .Select(hh => hh.Name)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        var members = await context.Users
            .Where(u => u.HouseholdId == householdId)
            .OrderBy(u => u.DisplayName)
            .Select(u => new MemberDto(u.Id, u.DisplayName, u.Initials, u.PictureUrl))
            .ToListAsync(ct);

        // The calculator windows completions to the local Mon–Sun week itself; fetch the household's
        // completions and let it filter (M1 scoping is ours, windowing is the calculator's).
        var completions = await context.ChoreCompletions
            .Where(c => c.HouseholdId == householdId)
            .ToListAsync(ct);

        var chores = await context.Chores
            .Where(c => c.HouseholdId == householdId && c.Status == ChoreStatus.Active)
            .ToListAsync(ct);

        var equityResult = equity.Compute(completions, members, EquityWindow.Week, now, tz);

        var choreDueness = new List<DigestChoreLine>(chores.Count);
        var upForGrabsCount = 0;

        foreach (var chore in chores)
        {
            var dueness = status.Compute(ChoreRecurrenceSnapshot.FromChore(chore), now, tz);

            // Exclude snoozed chores from the digest entirely (WP-04) — both the falling-behind line list AND the
            // up-for-grabs count. A snoozed chore reads Scheduled, so it carries no pressure worth reporting. The
            // guard lives HERE (the projection loop), not in DigestBuilder, which only sees {Name, DueState}.
            if (dueness.IsSnoozed)
            {
                continue;
            }

            choreDueness.Add(new DigestChoreLine(chore.Name, dueness.DueState));

            var isClaimStale = status.IsClaimStale(chore.AssignmentKind, chore.ClaimedAt, now);
            if (ChoreAttention.IsUpForGrabs(chore.AssignmentKind, isClaimStale))
            {
                upForGrabsCount++;
            }
        }

        return builder.Build(householdName, equityResult, choreDueness, upForGrabsCount);
    }

    /// <summary>
    /// Restore the household's <c>LastSentAt</c> to its pre-claim value after a failed build/decrypt/send, so
    /// a later hourly tick retries this window (M10 compensation). Atomic and scoped to this household.
    /// </summary>
    private async Task CompensateAsync(int householdId, DateTime? priorLastSentAt, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await context.ChoreDigestSettings
            .Where(s => s.HouseholdId == householdId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastSentAt, priorLastSentAt), ct);
    }

    /// <summary>Flat candidate snapshot — only the fields the due-check needs (M1 — id from the row only).</summary>
    private readonly record struct DigestCandidate(int HouseholdId, DayOfWeek SendDayOfWeek, int SendHourLocal, DateTime? LastSentAt);

    private enum ProcessOutcome { Sent, Skipped, Failed }
}
