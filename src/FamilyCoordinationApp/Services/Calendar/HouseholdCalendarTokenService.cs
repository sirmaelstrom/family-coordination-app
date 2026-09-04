using System.Security.Cryptography;
using System.Text;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FamilyCoordinationApp.Services.Calendar;

public sealed class HouseholdCalendarTokenService(IDbContextFactory<ApplicationDbContext> dbFactory) : IHouseholdCalendarTokenService
{
    public Task<CreatedCalendarToken> CreateOrRotateAsync(int householdId, CancellationToken ct = default) =>
        CreateOrRotateAsync(householdId, retryOnUniqueViolation: true, ct);

    private async Task<CreatedCalendarToken> CreateOrRotateAsync(
        int householdId,
        bool retryOnUniqueViolation,
        CancellationToken ct)
    {
        try
        {
            var token = CreateToken();
            var now = DateTime.UtcNow;
            await using var context = await dbFactory.CreateDbContextAsync(ct);
            await using var transaction = await context.Database.BeginTransactionAsync(ct);

            // Serialize same-household rotations so IX_HouseholdCalendarTokens_HouseholdId remains a last-resort guard.
            // TENANT-SCOPE-OK: the advisory lock is keyed by the authenticated caller's household id.
            await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({householdId})", ct);

            await context.HouseholdCalendarTokens
                .Where(calendarToken => calendarToken.HouseholdId == householdId && calendarToken.RevokedAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(calendarToken => calendarToken.RevokedAt, now), ct);

            context.HouseholdCalendarTokens.Add(new HouseholdCalendarToken
            {
                HouseholdId = householdId,
                TokenHash = Hash(token),
                CreatedAt = now,
            });

            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new CreatedCalendarToken(token, now);
        }
        catch (DbUpdateException ex) when (retryOnUniqueViolation && IsActiveTokenUniqueViolation(ex))
        {
            // IX_HouseholdCalendarTokens_HouseholdId can reject the losing READ COMMITTED rotation; retry once.
            return await CreateOrRotateAsync(householdId, retryOnUniqueViolation: false, ct);
        }
    }

    public async Task RevokeAsync(int householdId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        var active = await context.HouseholdCalendarTokens
            .Where(calendarToken => calendarToken.HouseholdId == householdId && calendarToken.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var token in active)
        {
            token.RevokedAt = now;
        }
        await context.SaveChangesAsync(ct);
    }

    public async Task<HouseholdCalendarToken?> ResolveActiveAsync(string? token, CancellationToken ct = default)
    {
        var hash = HashForLookup(token);

        await using var context = await dbFactory.CreateDbContextAsync(ct);
        // TENANT-SCOPE-OK: capability lookup by globally unique token hash is the scope source.
        return await context.HouseholdCalendarTokens
            .AsNoTracking()
            .Where(calendarToken => calendarToken.TokenHash == hash && calendarToken.RevokedAt == null)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<HouseholdCalendarToken?> GetActiveAsync(int householdId, CancellationToken ct = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        return await context.HouseholdCalendarTokens
            .AsNoTracking()
            .Where(calendarToken => calendarToken.HouseholdId == householdId && calendarToken.RevokedAt == null)
            .OrderByDescending(calendarToken => calendarToken.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    private static string CreateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string HashForLookup(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return ImpossibleHash;
        try
        {
            var bytes = Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + "=");
            return token.Length == 43 && bytes.Length == 32 && token.All(IsBase64UrlCharacter)
                ? Hash(token)
                : ImpossibleHash;
        }
        catch (FormatException)
        {
            return ImpossibleHash;
        }
    }

    private static bool IsBase64UrlCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '-' or '_';

    private static bool IsActiveTokenUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "IX_HouseholdCalendarTokens_HouseholdId",
        };

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private const string ImpossibleHash = "0000000000000000000000000000000000000000000000000000000000000000";
}
