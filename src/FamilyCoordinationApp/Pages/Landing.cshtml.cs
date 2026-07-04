using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FamilyCoordinationApp.Pages;

// De-Blazor WP-10: static Razor Page replacing Landing.razor. The authed → /dashboard redirect that
// ran in OnAfterRenderAsync (circuit) now runs server-side in OnGet. Owns "/" until WP-12 flips the
// SPA to root (then this is unmapped; the SPA index takes "/").
[AllowAnonymous]
public class LandingModel : PageModel
{
    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return Redirect("/dashboard");
        }

        return Page();
    }
}
