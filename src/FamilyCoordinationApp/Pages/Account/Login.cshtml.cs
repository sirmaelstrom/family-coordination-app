using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FamilyCoordinationApp.Pages.Account;

// De-Blazor WP-10: static Razor Page replacing Login.razor (Blazor/MudBlazor + InteractiveServer).
// The Google sign-in <form> still POSTs to the existing /account/login-google minimal-API challenge
// (Program.cs) — unchanged (MN7).
[AllowAnonymous]
public class LoginModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>
    /// A signed-in member asked to prove it is them again (minting an agent token needs a fresh sign-in, quest
    /// 8bfa42cf): show the page instead of skipping it, and ask Google to show the account chooser.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public bool Reauth { get; set; }

    public string SafeReturnUrl { get; private set; } = "/";

    public IActionResult OnGet()
    {
        SafeReturnUrl = Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/";

        // Already authenticated → skip the login page and go where they were headed
        // (local-only, so a crafted ?ReturnUrl can't drive an open redirect).
        if (User.Identity?.IsAuthenticated == true && !Reauth)
        {
            return LocalRedirect(SafeReturnUrl);
        }

        return Page();
    }
}
