using System.Security.Claims;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Pages.Account;

// De-Blazor WP-10: static Razor Page replacing AccessDenied.razor. The household / pending-request
// checks that were done in OnAfterRenderAsync (needing a circuit) now run server-side in OnGetAsync
// before the page renders — no InteractiveServer, no loading spinner.
[AllowAnonymous]
public class AccessDeniedModel : PageModel
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly ILogger<AccessDeniedModel> _logger;

    public AccessDeniedModel(IDbContextFactory<ApplicationDbContext> dbFactory, ILogger<AccessDeniedModel> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public bool IsAuthenticated { get; private set; }
    public bool IsInHousehold { get; private set; }
    public bool HasPendingRequest { get; private set; }

    public async Task OnGetAsync()
    {
        IsAuthenticated = User.Identity?.IsAuthenticated == true;
        if (!IsAuthenticated)
        {
            return;
        }

        var email = EmailAddress.Normalize(User.FindFirst(ClaimTypes.Email)?.Value ?? "");

        await using var db = await _dbFactory.CreateDbContextAsync();
        // TENANT-SCOPE-OK: identity lookup by the caller's own authenticated email — pre-household onboarding surface
        // (an unmarked page: no tenant exists here, D3)
        var existingUser = await db.Users.IgnoreQueryFilters(["Tenant"]).WhereEmailMatches(email).FirstOrDefaultAsync();
        IsInHousehold = existingUser != null;

        if (!IsInHousehold)
        {
            // The same newest-request selection as the pending page, so "Check Request Status" shows what it promises.
            var newest = await db.HouseholdRequests.WhereEmailMatches(email).NewestRequestOrDefaultAsync(_logger, email);
            HasPendingRequest = newest?.Status == HouseholdRequestStatus.Pending;
        }
    }
}
