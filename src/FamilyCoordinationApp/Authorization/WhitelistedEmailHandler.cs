using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using FamilyCoordinationApp.Data;

namespace FamilyCoordinationApp.Authorization;

/// <summary>
/// Implements AUTH-02: Validates user email claim against User table whitelist.
/// Only users with IsWhitelisted=true are granted access.
/// </summary>
public class WhitelistedEmailHandler : AuthorizationHandler<WhitelistedEmailRequirement>
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly ILogger<WhitelistedEmailHandler> _logger;

    public WhitelistedEmailHandler(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        ILogger<WhitelistedEmailHandler> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        WhitelistedEmailRequirement requirement)
    {
        // Extract email claim from authenticated user
        var emailClaim = context.User.FindFirst(ClaimTypes.Email);
        if (emailClaim is null)
        {
            _logger.LogWarning("No email claim found in user context");
            return; // Fail authorization silently
        }

        var email = emailClaim.Value;

        try
        {
            // Check database for whitelisted user
            using var dbContext = _dbFactory.CreateDbContext();
            var user = await dbContext.Users
                .FirstOrDefaultAsync(u => u.Email == email && u.IsWhitelisted);

            if (user is not null)
            {
                // Update last login timestamp
                user.LastLoginAt = DateTime.UtcNow;
                await dbContext.SaveChangesAsync();

                context.Succeed(requirement);
                _logger.LogInformation("User {Email} authorized successfully", email);
            }
            else
            {
                _logger.LogWarning("User {Email} not whitelisted or not found", email);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking whitelist for {Email}", email);
            // Fail authorization on error (safe default)
        }
    }
}
