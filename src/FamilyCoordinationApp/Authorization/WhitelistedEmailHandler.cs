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
public class WhitelistedEmailHandler : AuthorizationHandler<WhitelistedEmailRequirement>
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly ILogger<WhitelistedEmailHandler> _logger;
    private readonly SetupService _setupService;

    public WhitelistedEmailHandler(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        ILogger<WhitelistedEmailHandler> logger,
        SetupService setupService)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        _setupService = setupService;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        WhitelistedEmailRequirement requirement)
    {
        // If setup is not complete, allow authenticated users through
        if (!await _setupService.IsSetupCompleteAsync())
        {
            if (context.User.Identity?.IsAuthenticated == true)
            {
                _logger.LogInformation("Setup not complete - allowing authenticated user through");
                context.Succeed(requirement);
                return;
            }
        }

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
                // Extract profile data from claims
                var pictureClaim = context.User.FindFirst("urn:google:picture")?.Value;
                var displayName = context.User.FindFirst(ClaimTypes.Name)?.Value ?? user.DisplayName;

                // Update user profile on every login (Google profile may have changed)
                user.LastLoginAt = DateTime.UtcNow;
                user.PictureUrl = pictureClaim;
                user.Initials = ComputeInitials(displayName);
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

    private static string ComputeInitials(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return "?";

        var parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return "?";
        if (parts.Length == 1)
            return parts[0][0].ToString().ToUpperInvariant();

        // First and last name initials
        return $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
    }
}
