using Microsoft.EntityFrameworkCore;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services;

public class SetupService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly ILogger<SetupService> _logger;

    public SetupService(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        ILogger<SetupService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<bool> IsSetupCompleteAsync()
    {
        try
        {
            await using var context = await _dbFactory.CreateDbContextAsync();

            // Ensure database exists and is migrated
            await context.Database.MigrateAsync();

            // Setup is complete if at least one household exists
            return await context.Households.AnyAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking setup status");
            return false;
        }
    }

    public async Task<(Household household, User user)> CreateHouseholdAsync(
        string householdName,
        string userEmail,
        string displayName,
        string googleId)
    {
        _logger.LogInformation(
            "Starting household creation: Name={HouseholdName}, Email={Email}, GoogleId={GoogleId}",
            householdName, userEmail, googleId);

        await using var context = await _dbFactory.CreateDbContextAsync();

        // Check if user already exists
        var existingUser = await context.Users.FirstOrDefaultAsync(u => u.Email == userEmail);
        if (existingUser != null)
        {
            _logger.LogWarning("User {Email} already exists with ID {UserId}", userEmail, existingUser.Id);
            throw new InvalidOperationException($"User {userEmail} already exists");
        }

        var household = new Household
        {
            Name = householdName,
            CreatedAt = DateTime.UtcNow
        };
        context.Households.Add(household);
        await context.SaveChangesAsync();

        _logger.LogInformation("Created household ID {HouseholdId}", household.Id);

        var user = new User
        {
            HouseholdId = household.Id,
            Email = userEmail,
            DisplayName = displayName,
            GoogleId = googleId,
            IsWhitelisted = true,
            CreatedAt = DateTime.UtcNow,
            LastLoginAt = DateTime.UtcNow
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        _logger.LogInformation(
            "Created household '{HouseholdName}' (ID {HouseholdId}) with initial user {Email} (ID {UserId})",
            householdName, household.Id, userEmail, user.Id);

        return (household, user);
    }

    public async Task<Household?> GetHouseholdAsync()
    {
        await using var context = await _dbFactory.CreateDbContextAsync();
        return await context.Households.FirstOrDefaultAsync();
    }

    public async Task<User?> GetUserByEmailAsync(string email)
    {
        await using var context = await _dbFactory.CreateDbContextAsync();
        return await context.Users
            .Include(u => u.Household)
            .FirstOrDefaultAsync(u => u.Email == email);
    }
}
