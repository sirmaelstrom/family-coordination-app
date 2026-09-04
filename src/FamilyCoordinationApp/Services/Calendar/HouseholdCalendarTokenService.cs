using System.Security.Cryptography;
using System.Text;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Services.Calendar;

public sealed class HouseholdCalendarTokenService(IDbContextFactory<ApplicationDbContext> dbFactory) : IHouseholdCalendarTokenService
{
    public async Task<CreatedCalendarToken> CreateOrRotateAsync(int householdId, CancellationToken ct = default)
    {
        var token = CreateToken();
        var now = DateTime.UtcNow;
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);

        var created = new HouseholdCalendarToken
        {
            HouseholdId = householdId,
            TokenHash = Hash(token),
            CreatedAt = now,
        };
        context.HouseholdCalendarTokens.Add(created);

        var active = await context.HouseholdCalendarTokens
            .Where(calendarToken => calendarToken.HouseholdId == householdId && calendarToken.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var oldToken in active)
        {
            oldToken.RevokedAt = now;
        }

        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new CreatedCalendarToken(token, now);
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
        if (!TryHash(token, out var hash)) return null;

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

    private static bool TryHash(string? token, out string hash)
    {
        hash = string.Empty;
        if (string.IsNullOrWhiteSpace(token) || token.Length != 43) return false;
        try
        {
            var bytes = Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + "=");
            if (bytes.Length != 32) return false;
            hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
