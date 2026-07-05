using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// Household-creation request administration (strangler — lifts <c>HouseholdAdmin.razor</c>'s direct-EF logic into
/// a testable service). Short-lived contexts via the factory. The site-admin 403 gate is the endpoint's job (these
/// are global, not M1-scoped — review R-C8); the correctness-load-bearing parts live HERE: the atomic approval
/// transaction (R-C2) and the already-reviewed guard (R-C3).
/// </summary>
public sealed class HouseholdRequestService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<HouseholdRequestService> logger) : IHouseholdRequestService
{
    public async Task<HouseholdAdminData> GetDataAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Pending-first, then newest (parity HouseholdAdmin.LoadData :196-199). Global read — no HouseholdId on the
        // entity, gated by the endpoint's site-admin 403 (R-C8). Read-only ⇒ AsNoTracking (council R6).
        var requests = await context.HouseholdRequests
            .AsNoTracking()
            .OrderByDescending(r => r.Status == HouseholdRequestStatus.Pending)
            .ThenByDescending(r => r.RequestedAt)
            .ToListAsync(cancellationToken);

        // Include Users so the endpoint can project MemberCount = Users.Count (R-C8).
        var households = await context.Households
            .AsNoTracking()
            .Include(h => h.Users)
            .OrderBy(h => h.Name)
            .ToListAsync(cancellationToken);

        return new HouseholdAdminData(requests, households);
    }

    public async Task<ApproveResult> ApproveAsync(int requestId, string reviewerEmail, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        // R-C2: ONE transaction over household + user + request-status + categories. Multiple SaveChanges inside it
        // (the User FK needs household.Id, so the household must flush first), committed once at the end. A failure
        // anywhere — including the User unique-email constraint — rolls the whole thing back, so we never leave an
        // orphan household or a household with no categories. (Replaces the page's three separate commits.)
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var request = await context.HouseholdRequests
            .FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken);
        if (request is null)
        {
            return new ApproveResult(ReviewOutcome.NotFound, null);
        }

        // R-C3: only a still-pending request can be approved. A non-pending one (already approved/rejected, or just
        // approved by another admin on a stale view) is refused — this closes the duplicate-household race.
        if (request.Status != HouseholdRequestStatus.Pending)
        {
            return new ApproveResult(ReviewOutcome.AlreadyReviewed, null);
        }

        var now = DateTime.UtcNow;

        var household = new Household
        {
            Name = request.HouseholdName,
            CreatedAt = now,
        };
        context.Households.Add(household);
        await context.SaveChangesAsync(cancellationToken); // assign household.Id for the user FK below

        context.Users.Add(new User
        {
            HouseholdId = household.Id,
            Email = request.Email,
            DisplayName = request.DisplayName,
            GoogleId = request.GoogleId,
            IsWhitelisted = true,
            CreatedAt = now,
        });

        request.Status = HouseholdRequestStatus.Approved;
        request.ReviewedAt = now;
        request.ReviewedBy = reviewerEmail;

        // Seed the nine default categories on THIS context, inside the same transaction (R-C2) — not the old
        // separate-context SeedDefaultCategoriesAsync, which committed independently.
        SeedData.AddDefaultCategories(context, household.Id);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // The request's email is already a user (stale data, or a concurrent approve that created the user
            // first). The transaction is NOT committed; returning here disposes `transaction` → full rollback (no
            // orphan household), so the caller gets a clean 409 instead of an opaque 500 (council R1). The R-C3
            // status guard already closes the common stale-poll race; this closes the true-concurrent-approve one.
            logger.LogWarning(ex,
                "Approve {RequestId} blocked: email {Email} already in use — rolled back", requestId, request.Email);
            return new ApproveResult(ReviewOutcome.EmailInUse, null);
        }

        logger.LogInformation(
            "Approved household request {RequestId}: {HouseholdName} for {Email} by {Admin}",
            requestId, request.HouseholdName, request.Email, reviewerEmail);

        return new ApproveResult(ReviewOutcome.Ok, household);
    }

    /// <summary>True when an EF save failure is a PostgreSQL unique-constraint violation (SQLSTATE 23505).</summary>
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };

    public async Task<ReviewOutcome> RejectAsync(int requestId, string? reason, string reviewerEmail, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var request = await context.HouseholdRequests
            .FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken);
        if (request is null)
        {
            return ReviewOutcome.NotFound;
        }

        // R-C3: don't re-review an already-decided request.
        if (request.Status != HouseholdRequestStatus.Pending)
        {
            return ReviewOutcome.AlreadyReviewed;
        }

        request.Status = HouseholdRequestStatus.Rejected;
        request.ReviewedAt = DateTime.UtcNow;
        request.ReviewedBy = reviewerEmail;
        request.RejectionReason = reason; // OPTIONAL — null/empty is allowed (R-C7)
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Rejected household request {RequestId}: {HouseholdName} for {Email} by {Admin}. Reason: {Reason}",
            requestId, request.HouseholdName, request.Email, reviewerEmail, reason);

        return ReviewOutcome.Ok;
    }

    public async Task<CreateHouseholdResult> CreateHouseholdAsync(
        string householdName, string ownerEmail, string? ownerDisplayName, string createdByEmail,
        CancellationToken cancellationToken = default)
    {
        var name = householdName?.Trim() ?? "";
        var email = ownerEmail?.Trim().ToLowerInvariant() ?? "";
        if (name.Length == 0 || email.Length == 0)
        {
            return new CreateHouseholdResult(CreateHouseholdOutcome.InvalidInput, null);
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        // The owner email must be free across ALL households (cross-tenant read, like AddMemberAsync's guard): an
        // existing user already has a home, and the unique Users.Email constraint would reject the insert anyway.
        // Cheap pre-check for a clean 409 before opening the transaction.
        var emailTaken = await context.Users.AnyAsync(u => u.Email == email, cancellationToken);
        if (emailTaken)
        {
            return new CreateHouseholdResult(CreateHouseholdOutcome.EmailInUse, null);
        }

        var now = DateTime.UtcNow;

        // R-C2: household + owner + default categories commit atomically (same shape as ApproveAsync). The owner FK
        // needs household.Id, so the household flushes first; a failure anywhere rolls the whole thing back — no
        // orphan household, no household with no categories.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var household = new Household { Name = name, CreatedAt = now };
        context.Households.Add(household);
        await context.SaveChangesAsync(cancellationToken); // assign household.Id for the user FK below

        context.Users.Add(new User
        {
            HouseholdId = household.Id,
            Email = email,
            // Fall back to the email local-part when no display name is given (parity AddMemberAsync).
            DisplayName = string.IsNullOrWhiteSpace(ownerDisplayName) ? email.Split('@')[0] : ownerDisplayName!.Trim(),
            GoogleId = null, // set when the owner first logs in with Google (parity AddMemberAsync)
            IsWhitelisted = true,
            CreatedAt = now,
        });

        SeedData.AddDefaultCategories(context, household.Id);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A concurrent create/approve inserted the same email first. The transaction is NOT committed; returning
            // disposes it → full rollback (no orphan household), so the caller gets a clean 409 (parity ApproveAsync).
            logger.LogWarning(ex, "Create household blocked: email {Email} already in use — rolled back", email);
            return new CreateHouseholdResult(CreateHouseholdOutcome.EmailInUse, null);
        }

        // Seed the curated chore/room library AFTER the commit (parity SetupService.CreateHouseholdAsync — the seeder
        // opens its own factory context; idempotent per OQ3). Kept out of the transaction on purpose: a failure here
        // leaves a usable household (owner + categories) with an empty chore library, not an orphan — logged, not fatal.
        try
        {
            await SeedData.SeedChoresAndRoomsAsync(dbFactory, household.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Household {HouseholdId} created but chore/room seeding failed", household.Id);
        }

        logger.LogInformation(
            "Created household {HouseholdId} '{HouseholdName}' with owner {Email} by {Admin}",
            household.Id, name, email, createdByEmail);

        return new CreateHouseholdResult(CreateHouseholdOutcome.Ok, household);
    }
}
