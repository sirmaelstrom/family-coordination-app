using System.Security.Claims;
using FamilyCoordinationApp.Data;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Authorization;

/// <summary>
/// Development-ONLY authentication bypass for the SvelteKit dev loop (de-Blazor WP-03). Under <c>npm run dev</c>
/// the SPA is served from Vite (:5174) and proxies <c>/api</c> to the .NET host (:5077); the <c>FamilyApp.Auth</c>
/// cookie is bound to the .NET origin and is NOT sent to the Vite dev origin, so without this every <c>/api</c>
/// call in the dev loop would 401. When (and only when) the environment is Development, the request is
/// unauthenticated, and no real <c>FamilyApp.Auth</c> cookie is present, this injects a seed identity carrying
/// the claims downstream code reads (<see cref="ClaimTypes.Email"/>, <see cref="ClaimTypes.Name"/>,
/// <see cref="ClaimTypes.NameIdentifier"/>, optional picture).
///
/// <para>The identity is READ from config (<c>DevAuth:Email/Name/GoogleId/Picture</c>) or the first existing DB
/// user — it never fabricates a phantom user (if none exists it 503s rather than inventing one, so the dev
/// fixes their seed/config instead of getting a ghost session).</para>
///
/// <para><b>Security (MN3 / E1):</b> structurally inert outside Development. It is registered only inside an
/// <c>if (app.Environment.IsDevelopment())</c> block in Program.cs, re-checks <c>environment.IsDevelopment()</c>
/// here as defence-in-depth, and a fail-closed startup guard (<see cref="DevAuthStartupGuard"/>) throws if
/// <c>DEV_AUTH_BYPASS</c> is ever set in a non-Development environment. A Production no-cookie request therefore
/// always reaches authorization unauthenticated → 401 (see <see cref="ApiAwareAuthEvents"/>).</para>
/// </summary>
public sealed class DevAuthBypassMiddleware(RequestDelegate next, ILogger<DevAuthBypassMiddleware> logger)
{
    /// <summary>The real session cookie — its presence means a genuine session exists, so never override it.</summary>
    private const string AuthCookieName = "FamilyApp.Auth";

    /// <summary>The synthetic authentication type stamped on the injected development identity.</summary>
    public const string DevAuthenticationType = "DevAuthBypass";

    public async Task InvokeAsync(
        HttpContext context,
        IWebHostEnvironment environment,
        IConfiguration configuration,
        IDbContextFactory<ApplicationDbContext> dbFactory)
    {
        // Defence-in-depth: never authenticate a request outside Development, regardless of how this was wired.
        if (!environment.IsDevelopment())
        {
            await next(context);
            return;
        }

        // Only synthesize an identity for a genuinely anonymous request — never override a real principal, and
        // never touch a request that carries the real auth cookie (that must go through the real cookie handler).
        var alreadyAuthenticated = context.User.Identity?.IsAuthenticated == true;
        if (alreadyAuthenticated || context.Request.Cookies.ContainsKey(AuthCookieName))
        {
            await next(context);
            return;
        }

        var email = configuration["DevAuth:Email"];
        var name = configuration["DevAuth:Name"];
        var googleId = configuration["DevAuth:GoogleId"];
        var picture = configuration["DevAuth:Picture"];

        // No explicit config identity → enrich the FIRST existing DB user (do NOT invent one).
        if (string.IsNullOrWhiteSpace(email))
        {
            await using var db = await dbFactory.CreateDbContextAsync(context.RequestAborted);
            var seedUser = await db.Users
                .OrderBy(u => u.Id)
                .Select(u => new { u.Email, u.DisplayName })
                .FirstOrDefaultAsync(context.RequestAborted);

            if (seedUser is null || string.IsNullOrWhiteSpace(seedUser.Email))
            {
                logger.LogError(
                    "DevAuthBypass: no DevAuth:Email configured and no users exist in the database; cannot inject a " +
                    "dev identity. Seed a user or set DevAuth:Email.");
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(
                    new { message = "Dev auth bypass: no seed user available. Set DevAuth:Email or seed a user." });
                return;
            }

            email = seedUser.Email;
            name ??= seedUser.DisplayName;
            googleId ??= seedUser.Email;
        }

        name = string.IsNullOrWhiteSpace(name) ? email : name;
        googleId = string.IsNullOrWhiteSpace(googleId) ? email : googleId;

        var claims = new List<Claim>
        {
            new(ClaimTypes.Email, email),
            new(ClaimTypes.Name, name),
            new(ClaimTypes.NameIdentifier, googleId),
        };
        if (!string.IsNullOrWhiteSpace(picture))
        {
            claims.Add(new Claim("urn:google:picture", picture));
        }

        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, DevAuthenticationType));
        logger.LogWarning(
            "DevAuthBypass: injected DEVELOPMENT identity for {Email}. This must never run outside Development.",
            email);

        await next(context);
    }
}
