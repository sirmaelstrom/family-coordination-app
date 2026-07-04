using System.Security.Claims;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
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

    public AccessDeniedModel(IDbContextFactory<ApplicationDbContext> dbFactory) => _dbFactory = dbFactory;

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

        var email = User.FindFirst(ClaimTypes.Email)?.Value ?? "";

        await using var db = await _dbFactory.CreateDbContextAsync();
        var existingUser = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        IsInHousehold = existingUser != null;

        if (!IsInHousehold)
        {
            var request = await db.HouseholdRequests
                .FirstOrDefaultAsync(r => r.Email == email && r.Status == HouseholdRequestStatus.Pending);
            HasPendingRequest = request != null;
        }
    }
}
