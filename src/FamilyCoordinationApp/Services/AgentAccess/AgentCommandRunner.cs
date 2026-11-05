using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Configurations;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FamilyCoordinationApp.Services.AgentAccess;

/// <summary>How an agent write ended: the result, and whether it was only previewed or replayed from a retry.</summary>
public sealed record AgentWriteOutcome<T>(T Result, bool DryRun, bool Replayed);

/// <summary>
/// Runs one agent write (quest 8bfa42cf) in one database transaction shared by every service context it opens
/// (<see cref="AgentUnitOfWork"/>), together with its <see cref="AgentOperation"/> record:
/// <list type="bullet">
/// <item><b>Dry run</b>: the write runs through the same domain code and is rolled back. Nothing persists, not even
/// an empty meal plan, and no operation record is kept.</item>
/// <item><b>Commit</b>: the record is claimed FIRST, then the write runs, then the record stores the result, then one
/// commit. A concurrent request with the same key blocks on the record's unique index until this one commits, and is
/// then answered from the record. A failed write rolls back record and rows alike.</item>
/// <item><b>Retry</b>: the same member, request id and payload replays the stored result without running again; the
/// same id with a different payload is a <see cref="ConflictException"/>; an id older than
/// <see cref="RetentionWindow"/> is refused rather than run a second time.</item>
/// </list>
/// </summary>
public sealed partial class AgentCommandRunner(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    AgentUnitOfWork unitOfWork,
    ITenantContext tenant,
    IHouseholdClock clock,
    ILogger<AgentCommandRunner> logger)
{
    /// <summary>How long a request id stays answerable. Older ids are refused, never run again.</summary>
    public static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[A-Za-z0-9._:-]{8,100}$")]
    private static partial Regex RequestIdPattern();

    public async Task<AgentWriteOutcome<T>> RunAsync<T>(
        string tool,
        string? requestId,
        object payload,
        bool dryRun,
        Func<CancellationToken, Task<T>> write,
        CancellationToken ct = default)
    {
        if (tenant.State != TenantState.Caller || tenant.UserId is not { } userId)
        {
            throw new InvalidOperationException("An agent write needs the caller tenant.");
        }
        if (requestId is null || !RequestIdPattern().IsMatch(requestId))
        {
            throw new DomainValidationException(
                "requestId must be 8 to 100 characters of letters, digits, '.', '_', ':' or '-'. " +
                "Generate a new one for each distinct change, and reuse it only to retry the same change.");
        }

        var payloadHash = Hash(tool, payload);

        // The owner context opens and owns the connection; every context the services create while the unit of work
        // is active borrows it and enlists in the transaction.
        await using var owner = await dbFactory.CreateDbContextAsync(ct);
        await owner.Database.OpenConnectionAsync(ct);
        await using var transaction = await owner.Database.BeginTransactionAsync(ct);
        // Hands the owner context's open connection to TenantDbContextFactory so the services' contexts enlist in this
        // TENANT-SCOPE-OK: transaction; no SQL runs through it here, and every context built on it carries this scope's tenant.
        unitOfWork.Begin(owner.Database.GetDbConnection(), transaction.GetDbTransaction());
        try
        {
            if (dryRun)
            {
                var preview = await write(ct);
                await transaction.RollbackAsync(ct);
                return new AgentWriteOutcome<T>(preview, DryRun: true, Replayed: false);
            }

            if (await ReplayAsync<T>(owner, userId, requestId, tool, payloadHash, ct) is { } replayed)
            {
                await transaction.RollbackAsync(ct);
                return replayed;
            }

            var record = new AgentOperation
            {
                UserId = userId,
                RequestId = requestId,
                Tool = tool,
                PayloadHash = payloadHash,
                ResultJson = "null",
                CreatedAt = clock.UtcNow,
            };
            owner.AgentOperations.Add(record);
            try
            {
                // Claim the key. A concurrent identical request's uncommitted claim makes this wait; once it commits,
                // this is a unique violation and the committed record answers below.
                await owner.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (UniqueViolation.Of(ex)?.ConstraintName == AgentOperationConfiguration.RequestIndexName)
            {
                owner.Entry(record).State = EntityState.Detached;
                var winner = await ReplayAsync<T>(owner, userId, requestId, tool, payloadHash, ct)
                    ?? throw new ConflictException("That requestId is in use by another change. Use a new requestId.");
                await transaction.RollbackAsync(ct);
                return winner;
            }

            var result = await write(ct);
            record.ResultJson = JsonSerializer.Serialize(result, Json);
            await owner.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            logger.LogInformation("Agent write {Tool} committed for user {UserId} (request {RequestId})", tool, userId, requestId);
            return new AgentWriteOutcome<T>(result, DryRun: false, Replayed: false);
        }
        finally
        {
            unitOfWork.End();
        }
    }

    private async Task<AgentWriteOutcome<T>?> ReplayAsync<T>(
        ApplicationDbContext owner, int userId, string requestId, string tool, string payloadHash, CancellationToken ct)
    {
        var existing = await owner.AgentOperations
            .AsNoTracking()
            .Where(operation => operation.UserId == userId && operation.RequestId == requestId)
            .Select(operation => new { operation.Tool, operation.PayloadHash, operation.ResultJson, operation.CreatedAt })
            .FirstOrDefaultAsync(ct);
        if (existing is null) return null;

        if (existing.CreatedAt < clock.UtcNow - RetentionWindow)
        {
            throw new ConflictException(
                $"requestId {requestId} was used more than {RetentionWindow.TotalDays:0} days ago and has expired. " +
                "Read the current state, and use a new requestId if the change is still needed.");
        }
        if (existing.Tool != tool || existing.PayloadHash != payloadHash)
        {
            throw new ConflictException(
                $"requestId {requestId} was already used for a different change ({existing.Tool}). Nothing was changed. " +
                "Use a new requestId for a new change.");
        }

        logger.LogInformation("Agent write {Tool} replayed for user {UserId} (request {RequestId})", tool, userId, requestId);
        return new AgentWriteOutcome<T>(JsonSerializer.Deserialize<T>(existing.ResultJson, Json)!, DryRun: false, Replayed: true);
    }

    private static string Hash(string tool, object payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tool + "\n" + JsonSerializer.Serialize(payload, Json))));
}
