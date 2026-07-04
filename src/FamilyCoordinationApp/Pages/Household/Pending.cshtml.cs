using System.Security.Claims;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Pages.Household;

// De-Blazor WP-11: static Razor Page replacing Household/Pending.razor (InteractiveServer + IDisposable
// 30s timer). The page server-renders the current request status each load; the auto-poll becomes a
// tiny setTimeout(reload) in the view (only while Pending). When the request is approved OR the user is
// now provisioned into a household, OnGetAsync redirects into the app.
[AllowAnonymous]
public class PendingModel : PageModel
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;

    public PendingModel(IDbContextFactory<ApplicationDbContext> dbFactory) => _dbFactory = dbFactory;

    public bool IsAuthenticated { get; private set; }
    public HouseholdRequest? RequestRecord { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        IsAuthenticated = User.Identity?.IsAuthenticated == true;
        if (!IsAuthenticated)
        {
            return Page();
        }

        var email = User.FindFirst(ClaimTypes.Email)?.Value ?? "";

        await using var db = await _dbFactory.CreateDbContextAsync();

        // Already provisioned into a household → into the app.
        var existingUser = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (existingUser != null)
        {
            return Redirect("/");
        }

        RequestRecord = await db.HouseholdRequests
            .OrderByDescending(r => r.RequestedAt)
            .FirstOrDefaultAsync(r => r.Email == email);

        // Approved (but not yet provisioned) → still send them in.
        if (RequestRecord?.Status == HouseholdRequestStatus.Approved)
        {
            return Redirect("/");
        }

        return Page();
    }
}
