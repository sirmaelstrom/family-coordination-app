using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Services.AgentAccess;

/// <summary>The validated identity behind a personal access token, for one request.</summary>
public sealed record AgentTokenIdentity(int TokenId, int HouseholdId, int UserId, AgentTokenAccess Access);

/// <summary>Why a presented token was refused. Logged with the token id when known; never shown to the caller.</summary>
public enum AgentTokenRefusal
{
    Malformed,
    Unknown,
    Revoked,
    Expired,
    MemberDisabled,
    MemberMoved,
}

/// <summary>The outcome of checking a presented token: exactly one of the two is set.</summary>
public sealed record AgentTokenValidation(AgentTokenIdentity? Identity, AgentTokenRefusal? Refusal, int? TokenId)
{
    public static AgentTokenValidation Accepted(AgentTokenIdentity identity) => new(identity, null, identity.TokenId);
    public static AgentTokenValidation Refused(AgentTokenRefusal refusal, int? tokenId = null) => new(null, refusal, tokenId);
}

/// <summary>A request to mint a token. Exactly one of <paramref name="ExpiresInDays"/> and <paramref name="ExpiresOn"/>.</summary>
public sealed record MintAgentTokenCommand(string Label, AgentTokenAccess Access, int? ExpiresInDays, DateOnly? ExpiresOn);

/// <summary>The one response that carries the secret.</summary>
public sealed record MintedAgentToken(
    int Id, string Token, string Label, string SafePrefix, AgentTokenAccess Access, DateTime CreatedAt, DateTime ExpiresAt);

/// <summary>A token as its owner sees it later: never the secret, never the hash.</summary>
public sealed record AgentTokenSummary(
    int Id,
    string Label,
    string SafePrefix,
    AgentTokenAccess Access,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? RevokedAt,
    DateTime? LastUsedAt,
    string Status);

public interface IPersonalAccessTokenService
{
    /// <summary>Mint a token for the caller in the caller's household. The result is the only copy of the secret.</summary>
    Task<MintedAgentToken> MintAsync(int householdId, int userId, MintAgentTokenCommand command, CancellationToken ct = default);

    /// <summary>The caller's own tokens, newest first.</summary>
    Task<IReadOnlyList<AgentTokenSummary>> ListAsync(int userId, CancellationToken ct = default);

    /// <summary>Revoke one of the caller's own tokens. Idempotent; another member's or an unknown id is a 404.</summary>
    Task RevokeAsync(int userId, int tokenId, CancellationToken ct = default);

    /// <summary>Check a presented secret. Reads only; runs before any tenant exists.</summary>
    Task<AgentTokenValidation> ValidateAsync(string? token, CancellationToken ct = default);

    /// <summary>Record a successful use, at most once a minute. Runs under the token's caller tenant.</summary>
    Task TouchAsync(int tokenId, CancellationToken ct = default);
}

public sealed class PersonalAccessTokenService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IHouseholdClock clock) : IPersonalAccessTokenService
{
    /// <summary>The expiry presets the Settings page offers; a custom date may be anything up to the longest.</summary>
    public static readonly int[] ExpiryPresetDays = [30, 90, 365];
    public const int MaxLifetimeDays = 365;
    public const int MaxActiveTokensPerMember = 10;
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    public async Task<MintedAgentToken> MintAsync(int householdId, int userId, MintAgentTokenCommand command, CancellationToken ct = default)
    {
        var label = command.Label?.Trim() ?? string.Empty;
        if (label.Length == 0)
        {
            throw new DomainValidationException("Give the token a name, such as the client that will use it.");
        }
        if (label.Length > FieldLengths.PersonalAccessToken.Label)
        {
            throw new DomainValidationException($"The name can be at most {FieldLengths.PersonalAccessToken.Label} characters.");
        }
        if (!Enum.IsDefined(command.Access))
        {
            throw new DomainValidationException("Choose read-only or read and write access.");
        }

        var now = clock.UtcNow;
        var expiresAt = ResolveExpiry(householdId, command, now);

        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await HouseholdMembership.EnsureMemberAsync(context, householdId, userId, ct);

        var active = await context.PersonalAccessTokens
            .CountAsync(token => token.UserId == userId && token.RevokedAt == null && token.ExpiresAt > now, ct);
        if (active >= MaxActiveTokensPerMember)
        {
            throw new DomainValidationException(
                $"You already have {MaxActiveTokensPerMember} active agent tokens. Revoke one you no longer use first.");
        }

        var secret = AgentTokenFormat.Create();
        var row = new PersonalAccessToken
        {
            UserId = userId,
            Label = label,
            TokenHash = AgentTokenFormat.Hash(secret),
            SafePrefix = AgentTokenFormat.SafePrefixOf(secret),
            Access = command.Access,
            CreatedAt = now,
            ExpiresAt = expiresAt,
        };
        context.PersonalAccessTokens.Add(row);
        await context.SaveChangesAsync(ct);

        return new MintedAgentToken(row.Id, secret, row.Label, row.SafePrefix, row.Access, row.CreatedAt, row.ExpiresAt);
    }

    private DateTime ResolveExpiry(int householdId, MintAgentTokenCommand command, DateTime now)
    {
        switch (command)
        {
            case { ExpiresInDays: { } days, ExpiresOn: null }:
                if (!ExpiryPresetDays.Contains(days))
                {
                    throw new DomainValidationException("Choose an expiry of 30, 90 or 365 days, or a date.");
                }
                return now.AddDays(days);

            case { ExpiresInDays: null, ExpiresOn: { } date }:
                var today = clock.Today(householdId);
                if (date <= today)
                {
                    throw new DomainValidationException("The expiry date must be after today.");
                }
                if (date > today.AddDays(MaxLifetimeDays))
                {
                    throw new DomainValidationException($"A token can last at most {MaxLifetimeDays} days.");
                }
                // The end of that day in the household's zone: the token works through the date it names, but never longer
                // than the maximum lifetime from now (the last allowed date would otherwise overrun it by part of a day).
                var endOfDayLocal = DateTime.SpecifyKind(date.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
                var endOfDay = TimeZoneInfo.ConvertTimeToUtc(endOfDayLocal, clock.TimeZone(householdId));
                var ceiling = now.AddDays(MaxLifetimeDays);
                return endOfDay < ceiling ? endOfDay : ceiling;

            default:
                throw new DomainValidationException("Choose an expiry: 30, 90 or 365 days, or a date.");
        }
    }

    public async Task<IReadOnlyList<AgentTokenSummary>> ListAsync(int userId, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        // Every active token, always (at most MaxActiveTokensPerMember), so any credential that still works can be seen and
        // revoked; then the newest 50 revoked or expired ones as history.
        var active = await context.PersonalAccessTokens
            .AsNoTracking()
            .Where(token => token.UserId == userId && token.RevokedAt == null && token.ExpiresAt > now)
            .OrderByDescending(token => token.CreatedAt)
            .ThenByDescending(token => token.Id)
            .ToListAsync(ct);
        var history = await context.PersonalAccessTokens
            .AsNoTracking()
            .Where(token => token.UserId == userId && (token.RevokedAt != null || token.ExpiresAt <= now))
            .OrderByDescending(token => token.CreatedAt)
            .ThenByDescending(token => token.Id)
            .Take(50)
            .ToListAsync(ct);

        return active.Concat(history).Select(token => new AgentTokenSummary(
                token.Id,
                token.Label,
                token.SafePrefix,
                token.Access,
                token.CreatedAt,
                token.ExpiresAt,
                token.RevokedAt,
                token.LastUsedAt,
                token.RevokedAt is not null ? "revoked" : token.ExpiresAt <= now ? "expired" : "active"))
            .ToList();
    }

    public async Task RevokeAsync(int userId, int tokenId, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        // One statement, so a concurrent use-stamp (TouchAsync) can never turn a revoke into a version conflict.
        var revoked = await context.PersonalAccessTokens
            .Where(token => token.Id == tokenId && token.UserId == userId && token.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(token => token.RevokedAt, now), ct);
        if (revoked > 0) return;

        var exists = await context.PersonalAccessTokens.AnyAsync(token => token.Id == tokenId && token.UserId == userId, ct);
        if (!exists)
        {
            throw new NotFoundException("That agent token was not found.", $"token {tokenId} is not user {userId}'s");
        }
        // Already revoked: the state the caller wanted.
    }

    public async Task<AgentTokenValidation> ValidateAsync(string? token, CancellationToken ct = default)
    {
        if (!AgentTokenFormat.IsWellFormed(token))
        {
            return AgentTokenValidation.Refused(AgentTokenRefusal.Malformed);
        }

        var hash = AgentTokenFormat.Hash(token!);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        // TENANT-SCOPE-OK: credential lookup by globally unique token hash is the scope source. AgentTokenAuthenticationHandler
        // runs it before any tenant exists; the row it finds names the household, and the projection below re-checks
        // that the member still belongs to that household and is enabled.
        var row = await context.PersonalAccessTokens
            .IgnoreQueryFilters(["Tenant"])
            .AsNoTracking()
            .Where(candidate => candidate.TokenHash == hash)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.HouseholdId,
                candidate.UserId,
                candidate.Access,
                candidate.ExpiresAt,
                candidate.RevokedAt,
                MemberHouseholdId = candidate.User.HouseholdId,
                candidate.User.IsWhitelisted,
            })
            .FirstOrDefaultAsync(ct);

        if (row is null) return AgentTokenValidation.Refused(AgentTokenRefusal.Unknown);
        if (row.RevokedAt is not null) return AgentTokenValidation.Refused(AgentTokenRefusal.Revoked, row.Id);
        if (row.ExpiresAt <= clock.UtcNow) return AgentTokenValidation.Refused(AgentTokenRefusal.Expired, row.Id);
        if (row.MemberHouseholdId != row.HouseholdId) return AgentTokenValidation.Refused(AgentTokenRefusal.MemberMoved, row.Id);
        if (!row.IsWhitelisted) return AgentTokenValidation.Refused(AgentTokenRefusal.MemberDisabled, row.Id);

        return AgentTokenValidation.Accepted(new AgentTokenIdentity(row.Id, row.HouseholdId, row.UserId, row.Access));
    }

    public async Task TouchAsync(int tokenId, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var staleBefore = now - TouchInterval;
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        await context.PersonalAccessTokens
            .Where(token => token.Id == tokenId && (token.LastUsedAt == null || token.LastUsedAt < staleBefore))
            .ExecuteUpdateAsync(update => update.SetProperty(token => token.LastUsedAt, now), ct);
    }
}
