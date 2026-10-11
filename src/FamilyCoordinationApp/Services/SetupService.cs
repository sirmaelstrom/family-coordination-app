using Microsoft.EntityFrameworkCore;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services;

public class SetupService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    SetupCompletionLatch latch,
    ILogger<SetupService> logger,
    IHouseholdClock clock)
{

    /// <summary>
    /// Setup is complete once at least one household exists. Called twice per request (the first-run middleware
    /// and <c>WhitelistedEmailHandler</c>), so it must stay a cheap read: migrations are applied once at startup
    /// in <c>Program.cs</c>, not here, and the answer is latched after the first household is observed.
    /// </summary>
    public async Task<bool> IsSetupCompleteAsync()
    {
        if (latch.IsComplete)
            return true;

        try
        {
            await using var context = await dbFactory.CreateDbContextAsync();

            var complete = await context.Households.AnyAsync();
            if (complete)
                latch.MarkComplete();

            return complete;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking setup status");
            return false;
        }
    }

    public async Task<(Household household, User user)> CreateHouseholdAsync(
        string householdName,
        string userEmail,
        string displayName,
        string googleId)
    {
        userEmail = EmailAddress.Normalize(userEmail);
        logger.LogInformation(
            "Starting household creation: Name={HouseholdName}, Email={Email}, GoogleId={GoogleId}",
            householdName, userEmail, googleId);

        await using var context = await dbFactory.CreateDbContextAsync();

        // One transaction for the whole command (quest b942b943): the household, its owner, the default categories
        // and the chore/room library commit together or not at all. Before, each step committed on its own, so a
        // failure after the household save left an orphan household, and IsSetupCompleteAsync then answered true.
        // A bare BeginTransactionAsync is allowed: Program.cs's UseNpgsql configures no EnableRetryOnFailure.
        await using var transaction = await context.Database.BeginTransactionAsync();

        // Check if user already exists
        // TENANT-SCOPE-OK: first-run setup — the household is being created; identity is the authenticated email;
        // gated by IsSetupCompleteAsync at FirstRunSetup.cshtml.cs:52 (setup refuses once any household exists)
        var existingUser = await context.Users.IgnoreQueryFilters(["Tenant"]).WhereEmailMatches(userEmail).FirstOrDefaultAsync();
        if (existingUser != null)
        {
            logger.LogWarning("User {Email} already exists with ID {UserId}", userEmail, existingUser.Id);
            throw new InvalidOperationException($"User {userEmail} already exists");
        }

        var household = new Household
        {
            Name = householdName,
            CreatedAt = clock.UtcNow
        };
        context.Households.Add(household);
        await context.SaveChangesAsync(); // inside the transaction: assigns household.Id for RunAs and the user FK

        logger.LogInformation("Staged household ID {HouseholdId}", household.Id);

        // Everything from here is for the new household (D11): the user save and both seeds, all on this context.
        using var asNewHousehold = context.Tenant.RunAs(household.Id);

        var user = new User
        {
            Email = userEmail,
            DisplayName = displayName,
            GoogleId = googleId,
            IsWhitelisted = true,
            CreatedAt = clock.UtcNow,
            LastLoginAt = clock.UtcNow,
            Initials = UserProfile.ComputeInitials(displayName)
        };
        context.Users.Add(user);

        // The household is brand new, so the nine default categories are staged without the "already seeded?"
        // read (parity HouseholdRequestService's approval transaction).
        SeedData.AddDefaultCategories(context, household.Id);
        await context.SaveChangesAsync();

        // Seed the curated chore/room library (idempotent; OQ3 — seed at household creation). It reads the owner
        // saved above, which this transaction can see.
        await SeedData.SeedChoresAndRoomsAsync(context, household.Id);

        await transaction.CommitAsync();

        logger.LogInformation(
            "Created household '{HouseholdName}' (ID {HouseholdId}) with initial user {Email} (ID {UserId}), default categories, chores and rooms",
            householdName, household.Id, userEmail, user.Id);

        return (household, user);
    }

    public async Task<User?> GetUserByEmailAsync(string email)
    {
        email = EmailAddress.Normalize(email);
        await using var context = await dbFactory.CreateDbContextAsync();
        // TENANT-SCOPE-OK: identity lookup by email under the Tenant filter (no bypass): no caller in src or tests, so an unmarked future caller throws TenantNotSetException
        return await context.Users
            .Include(u => u.Household)
            .WhereEmailMatches(email)
            .SingleIdentityOrDefaultAsync(logger, email);
    }
}
