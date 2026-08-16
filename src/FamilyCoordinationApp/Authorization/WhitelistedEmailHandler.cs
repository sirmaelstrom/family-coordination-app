using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Authorization;

/// <summary>
/// Implements AUTH-02: Validates user email claim against User table whitelist.
/// Only users with IsWhitelisted=true are granted access.
/// During initial setup (no households exist), authenticated users are allowed through
/// to complete the setup process.
/// </summary>
/// <remarks>
/// This runs on every authorization evaluation, so it must READ ONLY. The profile refresh that used to happen
/// here now runs once per sign-in in <see cref="LoginProfileService"/>.
/// </remarks>
public class WhitelistedEmailHandler(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<WhitelistedEmailHandler> logger,
    SetupService setupService) : AuthorizationHandler<WhitelistedEmailRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        WhitelistedEmailRequirement requirement)
    {
        // If setup is not complete, allow authenticated users through
        if (!await setupService.IsSetupCompleteAsync())
        {
            if (context.User.Identity?.IsAuthenticated == true)
            {
                logger.LogInformation("Setup not complete - allowing authenticated user through");
                context.Succeed(requirement);
                return;
            }
        }

        // Extract email claim from authenticated user
        var emailClaim = context.User.FindFirst(ClaimTypes.Email);
        if (emailClaim is null)
        {
            logger.LogWarning("No email claim found in user context");
            return; // Fail authorization silently
        }

        var email = emailClaim.Value;

        try
        {
            // Check database for whitelisted user. Untracked: this path must not write.
            await using var dbContext = await dbFactory.CreateDbContextAsync();
            var whitelisted = await dbContext.Users
                .AsNoTracking()
                .AnyAsync(u => u.Email == email && u.IsWhitelisted);

            if (whitelisted)
            {
                context.Succeed(requirement);
                // Debug, not Information: this runs on every authorized request.
                logger.LogDebug("User {Email} authorized successfully", email);
            }
            else
            {
                logger.LogWarning("User {Email} not whitelisted or not found", email);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking whitelist for {Email}", email);
            // Fail authorization on error (safe default)
        }
    }
}
