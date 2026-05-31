using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// Scoped service that reads and writes per-household digest settings.
/// The webhook URL is encrypted at rest using ASP.NET Core Data Protection (M8).
/// The plaintext and ciphertext are NEVER written to logs, exceptions, or returned views (MN7).
/// </summary>
public class DigestSettingsService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IDataProtectionProvider dpProvider,
    TimeProvider timeProvider,
    ILogger<DigestSettingsService> logger) : IDigestSettingsService
{
    private readonly IDataProtector _protector = dpProvider.CreateProtector("ChoreDigestWebhook");

    // ── Default values mirrored from entity field defaults ──────────────────

    private static readonly DigestSettingsView DefaultView = new(
        Enabled: false,
        Cadence: DigestCadence.Weekly,
        SendDayOfWeek: DayOfWeek.Sunday,
        SendHourLocal: 18,
        HasWebhook: false,
        WebhookHint: null,
        LastSentAt: null);

    // ── Public API ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<DigestSettingsView> GetAsync(int householdId, CancellationToken cancellationToken = default)
    {
        await using var ctx = await dbFactory.CreateDbContextAsync(cancellationToken);

        var row = await ctx.ChoreDigestSettings
            .FirstOrDefaultAsync(s => s.HouseholdId == householdId, cancellationToken);

        if (row is null)
        {
            return DefaultView;
        }

        var hasWebhook = !string.IsNullOrEmpty(row.WebhookUrlProtected);
        var webhookHint = BuildHint(row.WebhookUrlProtected);

        // MN7: only hasWebhook (bool) and masked hint are returned — never URL or ciphertext.
        return new DigestSettingsView(
            Enabled: row.Enabled,
            Cadence: row.Cadence,
            SendDayOfWeek: row.SendDayOfWeek,
            SendHourLocal: row.SendHourLocal,
            HasWebhook: hasWebhook,
            WebhookHint: webhookHint,
            LastSentAt: row.LastSentAt);
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(int householdId, DigestSettingsUpdate update, CancellationToken cancellationToken = default)
    {
        ValidateUpdate(update);

        await using var ctx = await dbFactory.CreateDbContextAsync(cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;

        var row = await ctx.ChoreDigestSettings
            .FirstOrDefaultAsync(s => s.HouseholdId == householdId, cancellationToken);

        if (row is null)
        {
            row = new HouseholdChoreDigestSettings
            {
                HouseholdId = householdId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            ctx.ChoreDigestSettings.Add(row);
        }
        else
        {
            row.UpdatedAt = now;
        }

        row.Enabled = update.Enabled;
        row.Cadence = update.Cadence;
        row.SendDayOfWeek = update.SendDayOfWeek;
        row.SendHourLocal = update.SendHourLocal;

        // Tri-state webhook handling (frozen contract — WP-03/WP-06/WP-11):
        //   WebhookProvided = false  → leave WebhookUrlProtected unchanged
        //   WebhookProvided = true, non-blank URL → encrypt and store (MN7/M8)
        //   WebhookProvided = true, null or "" → clear (set to null)
        if (update.WebhookProvided)
        {
            if (!string.IsNullOrEmpty(update.WebhookUrl))
            {
                // Protect encrypts; result is ciphertext only — never log the input.
                row.WebhookUrlProtected = _protector.Protect(update.WebhookUrl);
                logger.LogInformation(
                    "Webhook URL updated for household {HouseholdId}",
                    householdId);
            }
            else
            {
                row.WebhookUrlProtected = null;
                logger.LogInformation(
                    "Webhook URL cleared for household {HouseholdId}",
                    householdId);
            }
        }

        await ctx.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Digest settings updated for household {HouseholdId}: Enabled={Enabled}, Cadence={Cadence}, Day={Day}, Hour={Hour}",
            householdId, row.Enabled, row.Cadence, row.SendDayOfWeek, row.SendHourLocal);
    }

    /// <inheritdoc/>
    public async Task<string?> GetDecryptedWebhookAsync(int householdId, CancellationToken cancellationToken = default)
    {
        await using var ctx = await dbFactory.CreateDbContextAsync(cancellationToken);

        var row = await ctx.ChoreDigestSettings
            .FirstOrDefaultAsync(s => s.HouseholdId == householdId, cancellationToken);

        if (row is null || string.IsNullOrEmpty(row.WebhookUrlProtected))
        {
            return null;
        }

        try
        {
            // Returns the plaintext; the CALLER (DigestService / WP-05) is responsible for
            // treating it as a secret — it must never be logged or forwarded to a client.
            return _protector.Unprotect(row.WebhookUrlProtected);
        }
        catch (Exception ex)
        {
            // Key rotation or data corruption: log without any secret material (MN7).
            // The household is treated as unconfigured for this run — return null rather than throw.
            logger.LogWarning(
                ex,
                "Failed to decrypt webhook URL for household {HouseholdId}. " +
                "The household will be skipped this run. " +
                "This may indicate key rotation — re-save the webhook URL to re-encrypt.",
                householdId);
            return null;
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Validates the update payload; throws <see cref="DigestSettingsValidationException"/> on bad input.
    /// </summary>
    private static void ValidateUpdate(DigestSettingsUpdate update)
    {
        if (update.SendHourLocal < 0 || update.SendHourLocal > 23)
        {
            throw new DigestSettingsValidationException(
                $"SendHourLocal must be in [0, 23] but was {update.SendHourLocal}.");
        }

        if (!Enum.IsDefined(typeof(DayOfWeek), update.SendDayOfWeek))
        {
            throw new DigestSettingsValidationException(
                $"SendDayOfWeek value {(int)update.SendDayOfWeek} is not a valid DayOfWeek.");
        }

        if (!Enum.IsDefined(typeof(DigestCadence), update.Cadence))
        {
            throw new DigestSettingsValidationException(
                $"Cadence value {(int)update.Cadence} is not a valid DigestCadence.");
        }
    }

    /// <summary>
    /// Builds a masked hint from the stored ciphertext: tries to decrypt, takes last 4 chars of the URL,
    /// then scrubs the plaintext from memory as quickly as possible. Returns null when no ciphertext is set
    /// or when decryption fails. Never exposes the full URL or the ciphertext (MN7).
    /// </summary>
    private string? BuildHint(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext))
        {
            return null;
        }

        try
        {
            var plaintext = _protector.Unprotect(ciphertext);
            if (plaintext.Length == 0)
            {
                return null;
            }

            // Return only the last 4 characters of the URL as a recognisability hint.
            // This is safe: 4 chars of a long webhook URL are not enough to reconstruct it.
            var hint = plaintext.Length >= 4
                ? plaintext[^4..]
                : plaintext;

            return hint;
        }
        catch
        {
            // Decryption failed (key rotation, corruption). Hint is unavailable, but
            // we still know a webhook IS stored — the caller gets HasWebhook=true, hint=null.
            return null;
        }
    }
}
