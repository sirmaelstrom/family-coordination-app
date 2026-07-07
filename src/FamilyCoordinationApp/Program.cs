using System.Net;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using FamilyCoordinationApp.Authorization;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Endpoints;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Digest;
using FamilyCoordinationApp.Services.Interfaces;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using Polly;

var builder = WebApplication.CreateBuilder(args);

// Data Protection configuration
// Keys are persisted to a volume-mounted directory and optionally protected with a certificate
var keyDirectory = new DirectoryInfo(
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 
        ".aspnet", "DataProtection-Keys"));

var dpBuilder = builder.Services.AddDataProtection()
    .SetApplicationName("FamilyCoordinationApp")
    .PersistKeysToFileSystem(keyDirectory);

// If DATAPROTECTION_CERT is set (base64 PFX), use it for key encryption
// This ensures keys remain usable across container recreations
var certBase64 = Environment.GetEnvironmentVariable("DATAPROTECTION_CERT");
if (!string.IsNullOrEmpty(certBase64))
{
    try
    {
        var certBytes = Convert.FromBase64String(certBase64);
        var cert = new X509Certificate2(certBytes, (string?)null, X509KeyStorageFlags.MachineKeySet);
        dpBuilder.ProtectKeysWithCertificate(cert);
        Console.WriteLine("Data Protection: Using certificate for key encryption");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Data Protection: Failed to load certificate: {ex.Message}");
    }
}
else if (builder.Environment.IsProduction())
{
    throw new InvalidOperationException(
        "DATAPROTECTION_CERT is required in production. Set it to a base64-encoded PFX certificate.");
}
else
{
    Console.WriteLine("Data Protection: Keys unprotected (development only)");
}

// Database configuration - DbContextFactory for Blazor Server thread safety
// Read password from Docker secret file if available, inject into connection string
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Database connection string not configured. Set ConnectionStrings__DefaultConnection environment variable.");

const string dockerSecretPath = "/run/secrets/postgres_password";
if (File.Exists(dockerSecretPath))
{
    var password = File.ReadAllText(dockerSecretPath).Trim();
    connectionString = connectionString.Contains("Password=", StringComparison.OrdinalIgnoreCase)
        ? connectionString
        : $"{connectionString.TrimEnd(';')};Password={password}";
    Console.WriteLine("Database: Password loaded from Docker secret");
}

builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString)
           .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

// Services
builder.Services.AddScoped<SetupService>();
builder.Services.AddScoped<IIngredientParser, IngredientParser>();
builder.Services.AddScoped<ICategoryInferenceService, CategoryInferenceService>();
builder.Services.AddScoped<IImageService, ImageService>();
builder.Services.AddScoped<IRecipeService, RecipeService>();
builder.Services.AddScoped<ICategoryService, CategoryService>();
builder.Services.AddScoped<IDraftService, DraftService>();
builder.Services.AddScoped<IMealPlanService, MealPlanService>();
// Meal-plan island (strangler): read-only board + entry/recipe projection (mirrors IChoreBoardService).
builder.Services.AddScoped<IMealPlanBoardService, MealPlanBoardService>();
// Recipes island (strangler): the single recipe→DTO projection (list card / full detail / parsed ingredient).
builder.Services.AddScoped<IRecipeProjectionService, RecipeProjectionService>();
// Dashboard island (strangler): the read-only aggregate (chore summary + shopping summary + today's meals).
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IShoppingListService, ShoppingListService>();
builder.Services.AddScoped<UnitConverter>();
builder.Services.AddScoped<IShoppingListGenerator, ShoppingListGenerator>();
builder.Services.AddScoped<IHouseholdConnectionService, HouseholdConnectionService>();
// Settings island A (strangler): household member management lifted out of WhitelistAdmin.razor's direct EF
// (the self / last-active / last-user guards are enforced here, server-side, and unit-testable).
builder.Services.AddScoped<IHouseholdMemberService, HouseholdMemberService>();
// Settings island C (strangler): household-request + feedback administration lifted out of HouseholdAdmin.razor /
// FeedbackAdmin.razor's direct EF. The load-bearing parts are server-enforced here: the atomic approval
// transaction (R-C2), the already-reviewed 409 guard (R-C3), and the feedback IDOR scope (R-C1).
builder.Services.AddScoped<IHouseholdRequestService, HouseholdRequestService>();
builder.Services.AddScoped<IFeedbackService, FeedbackService>();

// Chores (Phase 10): room CRUD, chore writes/state-machine, and the board read model. Date math is
// timezone-aware: the calculator is a stateless singleton; the household timezone (default America/Chicago,
// env-overridable via CHORES_TIMEZONE, D14) and TimeProvider.System are injected singletons consumed by the
// board service + the endpoint projection. Enum DTOs serialize as camelCase strings (see ConfigureHttpJsonOptions below).
builder.Services.AddScoped<IRoomService, RoomService>();
builder.Services.AddScoped<IChoreService, ChoreService>();
builder.Services.AddScoped<IChoreSubtaskService, ChoreSubtaskService>();
builder.Services.AddScoped<IChoreBoardService, ChoreBoardService>();
builder.Services.AddSingleton<ChoreStatusCalculator>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(ResolveChoresTimeZone(builder.Configuration));

// Chores v1.1 (WP-06): equity distribution + weekly Discord digest. The equity calculator and the digest
// builder are pure/stateless singletons (mirroring ChoreStatusCalculator); the settings service (webhook
// encryption) + run orchestration + sender are scoped. TimeProvider.System is already registered above.
builder.Services.AddSingleton<ChoreEquityCalculator>();
builder.Services.AddSingleton<ChorePlanningCalculator>();
builder.Services.AddSingleton<DigestBuilder>();
builder.Services.AddScoped<IDigestSettingsService, DigestSettingsService>();
builder.Services.AddScoped<IDigestService, DigestService>();
builder.Services.AddScoped<IDigestSender, DiscordWebhookDigestSender>();
// In-app weekly recap lens: read-only sibling of the digest (same DigestBuilder/equity/status pieces),
// adds the week-over-week trend. No webhook, no send — just GET /api/chores/recap.
builder.Services.AddScoped<IChoreRecapService, ChoreRecapService>();
// Chore-history surface (Phase 15): shared read-only aggregation behind the ledger (GET /api/chores/ledger)
// and the evolved recap. One computation, two projections (D3) — must NOT depend on IChoreRecapService.
builder.Services.AddScoped<IChoreHistoryService, ChoreHistoryService>();

// Chore/Room HTTP endpoints serialize enum DTOs (colorTier/dueState/assignmentKind/rollup status) as
// camelCase strings so responses match the island TS unions + the WP-05 board.json fixture (council M5/M11).
// Additive to the Minimal-API JSON options only — Blazor Server component rendering is unaffected.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(
        System.Text.Json.JsonNamingPolicy.CamelCase)));

// Presence tracker (singleton, in-memory). The Blazor-era DataNotifier/PollingService pub-sub died in
// WP-12 — staleness decay is now read-driven by GET /api/presence/users (see PresenceEndpoints).
builder.Services.AddSingleton<PresenceService>();

// Site admin service - config-based admin role
builder.Services.AddSingleton<ISiteAdminService, SiteAdminService>();

// Recipe import services
builder.Services.AddSingleton<IUrlValidator, UrlValidator>();
builder.Services.AddScoped<IRecipeScraperService, RecipeScraperService>();
builder.Services.AddScoped<IRecipeImportService, RecipeImportService>();

// YouTube recipe extraction services
builder.Services.AddScoped<IYtDlpService, YtDlpService>();
builder.Services.AddScoped<IDescriptionRecipeExtractor, DescriptionRecipeExtractor>();
builder.Services.AddScoped<IGeminiRecipeExtractor, GeminiRecipeExtractor>();
builder.Services.AddScoped<IYouTubeRecipeExtractor, YouTubeRecipeExtractor>();

// Named HttpClient for Gemini API. The API key is passed in the query string,
// so verbose HTTP logging is suppressed to prevent it from being logged.
builder.Services.AddHttpClient("Gemini", client =>
{
    client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Logging.AddFilter("System.Net.Http.HttpClient.Gemini", LogLevel.Warning);

// HttpClient for recipe scraping with Polly resilience
builder.Services.AddHttpClient("RecipeScraper")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    })
    .AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts = 3;
        options.Retry.BackoffType = DelayBackoffType.Exponential;
        options.Retry.UseJitter = true;
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);
        options.CircuitBreaker.FailureRatio = 0.5;
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
        options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
    });

// Named HttpClient for the Discord webhook digest sender (Chores v1.1 WP-06, M14). Mirrors the
// RecipeScraper resilience profile; consumed by DiscordWebhookDigestSender via IHttpClientFactory.
builder.Services.AddHttpClient("DiscordWebhook")
    .AddStandardResilienceHandler(o =>
    {
        o.Retry.MaxRetryAttempts = 3;
        o.Retry.BackoffType = DelayBackoffType.Exponential;
        o.Retry.UseJitter = true;
        o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
        o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
        o.CircuitBreaker.FailureRatio = 0.5;
        o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60); // must be >= 2 * AttemptTimeout
        o.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
    });

// Authentication
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = GoogleDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.Cookie.Name = "FamilyApp.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.ExpireTimeSpan = TimeSpan.FromDays(30);  // Persist session
    options.SlidingExpiration = true;
    options.LoginPath = "/account/login";
    options.LogoutPath = "/account/logout";
    options.AccessDeniedPath = "/account/access-denied";
    
    // Handle cookie decryption failures gracefully (e.g., after key rotation)
    // Instead of throwing, reject the cookie and force re-authentication
    options.Events.OnValidatePrincipal = context =>
    {
        // If we get here with a null principal, the cookie couldn't be decrypted
        // This happens when data protection keys have changed
        if (context.Principal == null)
        {
            context.RejectPrincipal();
        }
        return Task.CompletedTask;
    };

    // SPA support (de-Blazor WP-03): surface auth failures on /api as 401/403 instead of the 302 redirect the
    // SvelteKit session store cannot detect. The 401 for an ANONYMOUS /api request actually comes from the
    // Google handler (DefaultChallengeScheme = Google, see below); these cookie events cover the cookie-scheme
    // paths — the access-denied (403) forbid path falls back to the cookie scheme, and OnRedirectToLogin is here
    // as defence-in-depth for any flow that challenges the cookie scheme directly. Additive, /api-only (MN7).
    options.Events.OnRedirectToLogin = context =>
        ApiAwareAuthEvents.StatusForApiElseRedirect(context, StatusCodes.Status401Unauthorized);
    options.Events.OnRedirectToAccessDenied = context =>
        ApiAwareAuthEvents.StatusForApiElseRedirect(context, StatusCodes.Status403Forbidden);
})
.AddGoogle(options =>
{
    options.ClientId = builder.Configuration["Authentication:Google:ClientId"]
        ?? throw new InvalidOperationException("Google ClientId not configured");
    options.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"]
        ?? throw new InvalidOperationException("Google ClientSecret not configured");
    options.SaveTokens = false;  // Don't need refresh tokens for this app

    // Ensure email claim is mapped
    options.ClaimActions.MapJsonKey(ClaimTypes.Email, "email");
    options.ClaimActions.MapJsonKey(ClaimTypes.Name, "name");
    options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "sub");

    // Map picture claim for avatar display
    options.ClaimActions.MapJsonKey("urn:google:picture", "picture", "url");

    // Request email scope
    options.Scope.Add("email");
    options.Scope.Add("profile");
    
    // Handle OAuth failures gracefully (e.g., invalid state from key rotation)
    // Instead of showing an error page, redirect to login to start fresh
    options.Events.OnRemoteFailure = context =>
    {
        var logger = context.HttpContext.RequestServices.GetService<ILogger<Program>>();
        logger?.LogWarning("OAuth remote failure: {Message}. Redirecting to login.", 
            context.Failure?.Message ?? "Unknown error");
        
        // Clear any stale cookies that might cause issues
        context.Response.Cookies.Delete("FamilyApp.Auth");
        context.Response.Cookies.Delete(".AspNetCore.Correlation.Google");
        
        context.Response.Redirect("/account/login");
        context.HandleResponse();  // Prevent further processing
        return Task.CompletedTask;
    };

    // SPA support (de-Blazor WP-03): Google is the DefaultChallengeScheme, so an anonymous /api request that
    // fails RequireAuthorization is challenged HERE. For /api, return a bare 401 instead of the 302 redirect to
    // Google's OAuth consent — the SPA's session store detects the 401 and routes to /account/login. Browser
    // page routes still redirect to Google normally. Additive, /api-only; the OAuth challenge itself is
    // unchanged for pages (MN7).
    options.Events.OnRedirectToAuthorizationEndpoint = context =>
        ApiAwareAuthEvents.StatusForApiElseRedirect(context, StatusCodes.Status401Unauthorized);
});

// Authorization
builder.Services.AddScoped<IAuthorizationHandler, WhitelistedEmailHandler>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("WhitelistedOnly", policy =>
        policy.Requirements.Add(new WhitelistedEmailRequirement()));

    // Use default policy requiring authentication + whitelist
    // (Applied via [Authorize] on individual pages, not as FallbackPolicy to avoid blocking static assets)
    options.DefaultPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .AddRequirements(new WhitelistedEmailRequirement())
        .Build();
});

// Add services to the container.
builder.Services.AddHttpContextAccessor();

// De-Blazor WP-10/WP-11/WP-12: Razor Pages host the static peripheral pages (login/legal/error) and
// the onboarding flows. The Blazor Server runtime (Razor Components, InteractiveServer circuit,
// MudBlazor) was removed entirely in WP-12 — the UI is the SvelteKit SPA at the site root.
builder.Services.AddRazorPages();

// Fail-closed guard (de-Blazor WP-03): the dev-auth bypass is Development-ONLY. If DEV_AUTH_BYPASS is ever set
// in a non-Development environment (e.g. a mis-scoped deploy variable), refuse to start rather than risk an auth
// bypass in production. The bypass middleware itself is also only registered under IsDevelopment() below.
DevAuthStartupGuard.ThrowIfEnabledOutsideDevelopment(builder.Configuration, builder.Environment);

var app = builder.Build();

// Seed development data
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

    // Ensure database is created/migrated
    using var context = dbFactory.CreateDbContext();
    await context.Database.MigrateAsync();

    await SeedData.SeedDevelopmentDataAsync(dbFactory);
}

// Forwarded headers MUST come first (for nginx reverse proxy)
// Only trust proxies from Docker bridge networks and loopback (host nginx → container)
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedHeadersOptions.KnownProxies.Clear();
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("172.16.0.0"), 12)); // Docker bridge networks
forwardedHeadersOptions.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("127.0.0.0"), 8));   // Loopback
app.UseForwardedHeaders(forwardedHeadersOptions);

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found");
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAntiforgery();

// Auth middleware with graceful handling of corrupted/expired auth cookies
// This catches CryptographicException when data protection keys have rotated
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex) when (
        ex is System.Security.Cryptography.CryptographicException ||
        (ex.InnerException is System.Security.Cryptography.CryptographicException))
    {
        // Clear the corrupted auth cookie and redirect to login
        context.Response.Cookies.Delete("FamilyApp.Auth");
        context.Response.Cookies.Delete(".AspNetCore.Correlation.Google");
        
        // Log the issue
        var logger = context.RequestServices.GetService<ILogger<Program>>();
        logger?.LogWarning("Cleared corrupted auth cookie due to key rotation. User will need to re-authenticate.");
        
        // Redirect to login page
        context.Response.Redirect("/account/login");
    }
});
app.UseAuthentication();

// Dev-auth bypass (de-Blazor WP-03): Development-ONLY. Runs AFTER UseAuthentication (so it only acts on a still-
// anonymous request) and BEFORE the first-run-setup middleware + UseAuthorization (so the injected principal is
// present when authorization + the setup check evaluate it). Inert outside Development (see the middleware and
// the fail-closed startup guard above).
if (app.Environment.IsDevelopment())
{
    app.UseMiddleware<DevAuthBypassMiddleware>();
}

// First-run setup redirect middleware (BEFORE authorization)
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

    // Skip setup check for these paths (static assets, framework, auth, household request)
    if (path.StartsWith("/setup") ||
        path.StartsWith("/account") ||
        path.StartsWith("/household") ||
        path.StartsWith("/_") ||   // /_app SPA assets (Blazor's /_framework and /_blazor died in WP-12)
        path.StartsWith("/health") ||
        path.StartsWith("/lib") ||
        path.StartsWith("/css") ||
        path.StartsWith("/js") ||
        path.StartsWith("/images") ||
        path.EndsWith(".js") ||
        path.EndsWith(".css") ||
        path.EndsWith(".ico") ||
        path.EndsWith(".png") ||
        path.EndsWith(".svg") ||
        path.EndsWith(".woff") ||
        path.EndsWith(".woff2"))
    {
        await next();
        return;
    }

    var setupService = context.RequestServices.GetRequiredService<SetupService>();
    if (!await setupService.IsSetupCompleteAsync())
    {
        context.Response.Redirect("/setup");
        return;
    }

    await next();
});

app.UseAuthorization();

app.MapStaticAssets();

// De-Blazor WP-10/WP-11: static Razor Pages (login/legal/error/onboarding).
app.MapRazorPages();

app.MapMeEndpoints();
app.MapPresenceEndpoints();
app.MapShoppingListEndpoints();
app.MapChoresEndpoints();
app.MapRoomsEndpoints();
app.MapMealPlanEndpoints();
app.MapRecipesEndpoints();
app.MapDashboardEndpoints();
app.MapSettingsEndpoints();
app.MapSettingsConnectionsEndpoints();
app.MapSettingsAdminEndpoints();

// De-Blazor WP-12: the SvelteKit SPA owns the site root. EXPLICIT per-prefix fallbacks — NOT a broad
// root catch-all, which would shadow the Razor Pages (/account/*, /household/*, /setup, /privacy,
// /terms, /Error, /not-found) and turn unknown URLs into silent SPA loads instead of 404s. Client
// routes have no server endpoint, so any non-file request under an app prefix re-serves the SPA shell
// (wwwroot/index.html) and the client router takes over; /_app/* assets are served by static files
// first. The shell boots anonymously, then calls /api/me and bounces to /account/login on 401.
app.MapFallbackToFile("/", "index.html");
app.MapFallbackToFile("/dashboard/{**slug}", "index.html");
app.MapFallbackToFile("/chores/{**slug}", "index.html");
app.MapFallbackToFile("/shopping-list/{**slug}", "index.html");
app.MapFallbackToFile("/meal-plan/{**slug}", "index.html");
app.MapFallbackToFile("/recipes/{**slug}", "index.html");
app.MapFallbackToFile("/settings/{**slug}", "index.html");

// Old Blazor route → SPA route, so pre-flip bookmarks don't 404.
app.MapGet("/shoppinglists", () => Results.Redirect("/shopping-list", permanent: true));

// Health check endpoint for Docker
app.MapGet("/health", () => Results.Ok("healthy"));

// Auth endpoint (minimal API) — the login/onboarding Razor Pages' sign-in forms POST here.
// Login-CSRF hardening: initiating the OAuth challenge requires a valid antiforgery token (every sign-in
// form embeds one via @Html.AntiForgeryToken()), so a cross-site page cannot silently log the victim's
// browser into an attacker-chosen Google account. Validation is done in-handler via IAntiforgery — this
// endpoint has no form-binding metadata, so the UseAntiforgery middleware never validates it implicitly.
app.MapPost("/account/login-google", async (HttpContext context, IAntiforgery antiforgery) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(context);
    }
    catch (AntiforgeryValidationException)
    {
        // Non-empty body: an empty 4xx would re-execute through the GET-only /not-found page
        // (CORRECTIONS fca-empty-404-surfaces-as-405-on-delete).
        return Results.BadRequest(new { message = "Invalid or missing antiforgery token." });
    }

    // Get returnUrl from form, default to home
    var form = await context.Request.ReadFormAsync();
    var returnUrl = form["returnUrl"].FirstOrDefault() ?? "/";

    // Prevent open redirect - only allow local paths
    returnUrl = returnUrl.Trim();
    if (string.IsNullOrEmpty(returnUrl) ||
        !returnUrl.StartsWith('/') ||
        returnUrl.StartsWith("//") ||
        returnUrl.StartsWith("/\\") ||
        returnUrl.Contains("://"))
    {
        returnUrl = "/";
    }

    var properties = new AuthenticationProperties
    {
        RedirectUri = returnUrl,
        IsPersistent = true  // Remember me
    };
    // The Challenge redirect to Google (and the callback path) is unchanged — only the initiation is gated.
    return Results.Challenge(properties, [GoogleDefaults.AuthenticationScheme]);
});

app.MapGet("/account/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/account/login");
});

app.Run();

// Resolve the household timezone for chore day-boundary math (D14). Default America/Chicago; overridable via
// the CHORES_TIMEZONE config/env value (IANA id, with a Windows-id fallback so it resolves on either OS).
// Falls back to the default on an unrecognized id rather than crashing startup.
static TimeZoneInfo ResolveChoresTimeZone(IConfiguration configuration)
{
    const string defaultId = "America/Chicago";
    const string windowsFallbackId = "Central Standard Time";

    var configured = configuration["CHORES_TIMEZONE"];
    var requested = string.IsNullOrWhiteSpace(configured) ? defaultId : configured.Trim();

    if (TryFindTimeZone(requested, out var tz)) return tz;
    if (requested != defaultId && TryFindTimeZone(defaultId, out var def)) return def;
    if (TryFindTimeZone(windowsFallbackId, out var win)) return win;
    return TimeZoneInfo.Utc;

    static bool TryFindTimeZone(string id, out TimeZoneInfo tz)
    {
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            tz = TimeZoneInfo.Utc;
            return false;
        }
    }
}

// Expose the top-level-program entry type so the test project's WebApplicationFactory<Program> can reference
// it (WP-08 integration harness). This is the standard, robust workaround and is inert at runtime.
public partial class Program { }
