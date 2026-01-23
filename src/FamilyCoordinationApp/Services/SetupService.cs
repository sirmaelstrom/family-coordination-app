using Microsoft.EntityFrameworkCore;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services;

public class SetupService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly ILogger<SetupService> _logger;

    // Cache setup status (reset on household creation)
    private bool? _isSetupComplete;

    public SetupService(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        ILogger<SetupService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<bool> IsSetupCompleteAsync()
    {
        if (_isSetupComplete.HasValue)
            return _isSetupComplete.Value;

        try
        {
            using var context = _dbFactory.CreateDbContext();

            // Ensure database exists and is migrated
            await context.Database.MigrateAsync();

            // Setup is complete if at least one household exists
            _isSetupComplete = await context.Households.AnyAsync();
            return _isSetupComplete.Value;
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
        using var context = _dbFactory.CreateDbContext();

        var household = new Household
        {
            Name = householdName,
            CreatedAt = DateTime.UtcNow
        };
        context.Households.Add(household);
        await context.SaveChangesAsync();

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

        // Reset cache
        _isSetupComplete = true;

        _logger.LogInformation(
            "Created household '{HouseholdName}' with initial user {Email}",
            householdName, userEmail);

        return (household, user);
    }

    public async Task<Household?> GetHouseholdAsync()
    {
        using var context = _dbFactory.CreateDbContext();
        return await context.Households.FirstOrDefaultAsync();
    }

    public async Task<User?> GetUserByEmailAsync(string email)
    {
        using var context = _dbFactory.CreateDbContext();
        return await context.Users
            .Include(u => u.Household)
            .FirstOrDefaultAsync(u => u.Email == email);
    }
}
