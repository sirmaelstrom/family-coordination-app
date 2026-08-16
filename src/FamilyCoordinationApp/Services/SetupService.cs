using Microsoft.EntityFrameworkCore;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services;

public class SetupService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    SetupCompletionLatch latch,
    ILogger<SetupService> logger)
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
        logger.LogInformation(
            "Starting household creation: Name={HouseholdName}, Email={Email}, GoogleId={GoogleId}",
            householdName, userEmail, googleId);

        await using var context = await dbFactory.CreateDbContextAsync();

        // Check if user already exists
        var existingUser = await context.Users.FirstOrDefaultAsync(u => u.Email == userEmail);
        if (existingUser != null)
        {
            logger.LogWarning("User {Email} already exists with ID {UserId}", userEmail, existingUser.Id);
            throw new InvalidOperationException($"User {userEmail} already exists");
        }

        var household = new Household
        {
            Name = householdName,
            CreatedAt = DateTime.UtcNow
        };
        context.Households.Add(household);
        await context.SaveChangesAsync();

        logger.LogInformation("Created household ID {HouseholdId}", household.Id);

        var user = new User
        {
            HouseholdId = household.Id,
            Email = userEmail,
            DisplayName = displayName,
            GoogleId = googleId,
            IsWhitelisted = true,
            CreatedAt = DateTime.UtcNow,
            LastLoginAt = DateTime.UtcNow,
            Initials = UserProfile.ComputeInitials(displayName)
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        logger.LogInformation(
            "Created household '{HouseholdName}' (ID {HouseholdId}) with initial user {Email} (ID {UserId})",
            householdName, household.Id, userEmail, user.Id);

        // Seed default categories for the new household
        await SeedData.SeedDefaultCategoriesAsync(dbFactory, household.Id);
        logger.LogInformation("Seeded default categories for household {HouseholdId}", household.Id);

        // Seed the curated chore/room library (idempotent; OQ3 — seed at household creation).
        await SeedData.SeedChoresAndRoomsAsync(dbFactory, household.Id);
        logger.LogInformation("Seeded default chores and rooms for household {HouseholdId}", household.Id);

        return (household, user);
    }

    public async Task<User?> GetUserByEmailAsync(string email)
    {
        await using var context = await dbFactory.CreateDbContextAsync();
        return await context.Users
            .Include(u => u.Household)
            .FirstOrDefaultAsync(u => u.Email == email);
    }
}
