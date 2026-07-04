using System.Security.Claims;
using FamilyCoordinationApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FamilyCoordinationApp.Pages.Setup;

// De-Blazor WP-11: static Razor Page replacing Setup/FirstRunSetup.razor. OnPostAsync reuses
// SetupService.CreateHouseholdAsync; identity comes from HttpContext.User claims server-side (NOT form
// fields). Razor Pages auto-validate the antiforgery token on POST (C-b).
[AllowAnonymous]
public class FirstRunSetupModel : PageModel
{
    private readonly SetupService _setupService;
    private readonly ILogger<FirstRunSetupModel> _logger;

    public FirstRunSetupModel(SetupService setupService, ILogger<FirstRunSetupModel> logger)
    {
        _setupService = setupService;
        _logger = logger;
    }

    public bool IsAlreadySetup { get; private set; }
    public bool IsAuthenticated { get; private set; }
    public string UserEmail { get; private set; } = "";

    [BindProperty]
    public string? HouseholdName { get; set; }

    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        IsAlreadySetup = await _setupService.IsSetupCompleteAsync();
        if (IsAlreadySetup)
        {
            return Page();
        }

        IsAuthenticated = User.Identity?.IsAuthenticated == true;
        if (IsAuthenticated)
        {
            UserEmail = User.FindFirst(ClaimTypes.Email)?.Value ?? "";
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        IsAlreadySetup = await _setupService.IsSetupCompleteAsync();
        if (IsAlreadySetup)
        {
            return Redirect("/dashboard");
        }

        IsAuthenticated = User.Identity?.IsAuthenticated == true;
        if (!IsAuthenticated)
        {
            return Redirect("/account/login?ReturnUrl=%2Fsetup");
        }

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
            await _setupService.CreateHouseholdAsync(HouseholdName.Trim(), UserEmail, displayName, googleId);
            _logger.LogInformation("First-run setup created household for {Email}", UserEmail);
            return Redirect("/");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "First-run setup failed for {Email}", UserEmail);
            ErrorMessage = "Failed to create household. Please try again.";
            return Page();
        }
    }
}
