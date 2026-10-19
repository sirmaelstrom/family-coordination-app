using System.Security.Cryptography;
using System.Text;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Configurations;
using FamilyCoordinationApp.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FamilyCoordinationApp.Services.Calendar;

public sealed class HouseholdCalendarTokenService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IHouseholdClock clock) : IHouseholdCalendarTokenService
{
    public Task<CreatedCalendarToken> CreateOrRotateAsync(int householdId, int userId, CalendarFeed feed, CancellationToken ct = default) =>
        CreateOrRotateAsync(householdId, userId, feed, retryOnUniqueViolation: true, ct);

    private async Task<CreatedCalendarToken> CreateOrRotateAsync(
        int householdId,
        int userId,
        CalendarFeed feed,
        bool retryOnUniqueViolation,
        CancellationToken ct)
    {
        try
        {
            var token = CreateToken();
            var now = clock.UtcNow;
            await using var context = await dbFactory.CreateDbContextAsync(ct);
            await using var transaction = await context.Database.BeginTransactionAsync(ct);

            // Serialize same-household rotations so the active-token index remains a last-resort guard.
            // TENANT-SCOPE-OK: the advisory lock is keyed by the authenticated caller's household id.
            await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({householdId})", ct);

            // The member must belong to the tenant's household (the Users read is Tenant-filtered), so the row the
            // write step stamps with that household names a user of it: the feed's resolve requires exactly that.
            await HouseholdMembership.EnsureMemberAsync(context, householdId, userId, ct);

            // Only the caller's own link for this feed: another member's link, and the legacy shared link, stay
            // active (quest 8dba87f4).
            await context.HouseholdCalendarTokens
                .Where(calendarToken => calendarToken.UserId == userId && calendarToken.Feed == feed && calendarToken.RevokedAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(calendarToken => calendarToken.RevokedAt, now), ct);

            context.HouseholdCalendarTokens.Add(new HouseholdCalendarToken
            {
                UserId = userId,
                Feed = feed,
                TokenHash = Hash(token),
                CreatedAt = now,
            });

            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new CreatedCalendarToken(token, now);
        }
        catch (DbUpdateException ex) when (retryOnUniqueViolation && IsActiveTokenUniqueViolation(ex))
        {
            // The active-token index can reject the losing READ COMMITTED rotation; retry once.
            return await CreateOrRotateAsync(householdId, userId, feed, retryOnUniqueViolation: false, ct);
        }
    }

    public Task RevokeAsync(int userId, CalendarFeed feed, CancellationToken ct = default) =>
        RevokeWhereAsync(userId, feed, ct);

    public Task RevokeSharedAsync(CalendarFeed feed, CancellationToken ct = default) =>
        RevokeWhereAsync(userId: null, feed, ct);

    private async Task RevokeWhereAsync(int? userId, CalendarFeed feed, CancellationToken ct)
    {
        var now = clock.UtcNow;
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        var active = await context.HouseholdCalendarTokens
            .Where(calendarToken => calendarToken.UserId == userId && calendarToken.Feed == feed && calendarToken.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var token in active)
        {
            token.RevokedAt = now;
        }
        // Checked against the loaded xmin. A conflict is either a rotation (a real conflict: 409) or another revoke
        // (the state this call wanted). Tell them apart by what is persisted now (quest f1dc7a24).
        try
        {
            await context.SaveChangesOrConflictAsync(ct);
        }
        catch (ConcurrencyConflictException)
        {
            await using var check = await dbFactory.CreateDbContextAsync(ct);
            if (await check.HouseholdCalendarTokens.AnyAsync(calendarToken =>
                    calendarToken.UserId == userId && calendarToken.Feed == feed && calendarToken.RevokedAt == null, ct))
            {
                throw; // a rotation landed: a new active token exists that this revoke never saw
            }
            // Another revoke landed first: no active token remains, so the double-clicked revoke succeeds.
        }
    }

    public async Task<HouseholdCalendarToken?> ResolveActiveAsync(string? token, CalendarFeed feed, CancellationToken ct = default)
    {
        var hash = HashForLookup(token);

        await using var context = await dbFactory.CreateDbContextAsync(ct);
        // Only the meal plan ever had a household-shared (userless) link; every other feed is personal (3bdbcab7).
        var sharedAllowed = feed == CalendarFeed.MealPlan;
        // TENANT-SCOPE-OK: capability lookup by globally unique token hash is the scope source. The anonymous feeds
        // (CalendarTokenEndpoints GetFeed and GetChoresFeed, unmarked) have no tenant until this row names one. The
        // bypass is query-wide, so the User navigation is read unfiltered too; its predicate pins that user to the
        // token's own household and requires the member to be enabled (quest 8dba87f4).
        return await context.HouseholdCalendarTokens
            .IgnoreQueryFilters(["Tenant"])
            .AsNoTracking()
            .Where(calendarToken => calendarToken.TokenHash == hash && calendarToken.Feed == feed && calendarToken.RevokedAt == null
                && ((calendarToken.UserId == null && sharedAllowed)
                    || (calendarToken.User!.HouseholdId == calendarToken.HouseholdId && calendarToken.User.IsWhitelisted)))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<HouseholdCalendarToken?> GetActiveAsync(int userId, CalendarFeed feed, CancellationToken ct = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        return await context.HouseholdCalendarTokens
            .AsNoTracking()
            .Where(calendarToken => calendarToken.UserId == userId && calendarToken.Feed == feed && calendarToken.RevokedAt == null)
            .OrderByDescending(calendarToken => calendarToken.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<HouseholdCalendarToken?> GetActiveSharedAsync(CalendarFeed feed, CancellationToken ct = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        return await context.HouseholdCalendarTokens
            .AsNoTracking()
            .Where(calendarToken => calendarToken.UserId == null && calendarToken.Feed == feed && calendarToken.RevokedAt == null)
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
            ConstraintName: HouseholdCalendarTokenConfiguration.ActiveTokenIndexName,
        };

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private const string ImpossibleHash = "0000000000000000000000000000000000000000000000000000000000000000";
}
