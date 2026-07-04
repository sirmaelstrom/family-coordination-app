using System.Security.Claims;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Pages.Household;

// De-Blazor WP-11: static Razor Page replacing Household/Request.razor (InteractiveServer + MudBlazor).
// The household-create submit is an OnPostAsync handler — Razor Pages auto-validate the antiforgery
// token (C-b: tokens, NOT DisableAntiforgery), and identity (email/displayName/googleId) is read from
// HttpContext.User claims server-side, never trusted from form fields. Same EF writes as the original;
// no new domain logic (MN5). No separate minimal-API endpoint is needed — the Razor Page handler IS the
// POST target (a justified deviation from the spec's OnboardingEndpoints.cs, which predated the C-a
// Razor-Pages decision).
[AllowAnonymous]
public class RequestModel : PageModel
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly ILogger<RequestModel> _logger;

    public RequestModel(IDbContextFactory<ApplicationDbContext> dbFactory, ILogger<RequestModel> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public bool IsAuthenticated { get; private set; }
    public bool IsAlreadyInHousehold { get; private set; }
    public string UserEmail { get; private set; } = "";

    [BindProperty]
    public string? HouseholdName { get; set; }

    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        IsAuthenticated = User.Identity?.IsAuthenticated == true;
        if (!IsAuthenticated)
        {
            return Page();
        }

        UserEmail = User.FindFirst(ClaimTypes.Email)?.Value ?? "";

        await using var db = await _dbFactory.CreateDbContextAsync();
        var existingUser = await db.Users.FirstOrDefaultAsync(u => u.Email == UserEmail);
        if (existingUser != null)
        {
            IsAlreadyInHousehold = true;
            return Page();
        }

        var pending = await db.HouseholdRequests
            .FirstOrDefaultAsync(r => r.Email == UserEmail && r.Status == HouseholdRequestStatus.Pending);
        if (pending != null)
        {
            // Already have a pending request — go straight to the status page.
            return Redirect("/household/pending");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        IsAuthenticated = User.Identity?.IsAuthenticated == true;
        if (!IsAuthenticated)
        {
            return Redirect("/account/login?ReturnUrl=%2Fhousehold%2Frequest");
        }

        // Identity is resolved server-side from claims — the form supplies only the household name.
        UserEmail = User.FindFirst(ClaimTypes.Email)?.Value ?? "";
        var displayName = User.FindFirst(ClaimTypes.Name)?.Value ?? UserEmail;
        var googleId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";

        if (string.IsNullOrWhiteSpace(HouseholdName))
        {
            ErrorMessage = "Please enter a household name.";
            return Page();
        }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var existingUser = await db.Users.FirstOrDefaultAsync(u => u.Email == UserEmail);
            if (existingUser != null)
            {
                IsAlreadyInHousehold = true;
                return Page();
            }

            var existing = await db.HouseholdRequests.FirstOrDefaultAsync(r => r.Email == UserEmail);
            if (existing != null)
            {
                if (existing.Status == HouseholdRequestStatus.Pending)
                {
                    return Redirect("/household/pending");
                }

                if (existing.Status == HouseholdRequestStatus.Rejected)
                {
                    // Allow resubmission after a rejection (same behavior as the original page).
                    existing.HouseholdName = HouseholdName.Trim();
                    existing.DisplayName = displayName;
                    existing.GoogleId = googleId;
                    existing.Status = HouseholdRequestStatus.Pending;
                    existing.RequestedAt = DateTime.UtcNow;
                    existing.ReviewedAt = null;
                    existing.ReviewedBy = null;
                    existing.RejectionReason = null;
                }
            }
            else
            {
                db.HouseholdRequests.Add(new HouseholdRequest
                {
                    Email = UserEmail,
                    DisplayName = displayName,
                    GoogleId = googleId,
                    HouseholdName = HouseholdName.Trim(),
                    Status = HouseholdRequestStatus.Pending,
                    RequestedAt = DateTime.UtcNow
                });
            }

            await db.SaveChangesAsync();
            _logger.LogInformation("Household request submitted by {Email} for '{HouseholdName}'", UserEmail, HouseholdName);
            return Redirect("/household/pending");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit household request for {Email}", UserEmail);
            ErrorMessage = "Failed to submit request. Please try again.";
            return Page();
        }
    }
}
