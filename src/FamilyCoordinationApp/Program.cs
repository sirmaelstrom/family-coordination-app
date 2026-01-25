using System.Net;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using FamilyCoordinationApp.Authorization;
using FamilyCoordinationApp.Components;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using MudBlazor.Services;
using Plk.Blazor.DragDrop;
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
else
{
    Console.WriteLine("Data Protection: Keys unprotected (set DATAPROTECTION_CERT for production)");
}

// Database configuration - DbContextFactory for Blazor Server thread safety
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Database connection string not configured. Set ConnectionStrings__DefaultConnection environment variable.");

builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

// Services
builder.Services.AddScoped<SetupService>();
builder.Services.AddScoped<IIngredientParser, IngredientParser>();
builder.Services.AddScoped<ICategoryInferenceService, CategoryInferenceService>();
builder.Services.AddScoped<IImageService, ImageService>();
builder.Services.AddScoped<IRecipeService, RecipeService>();
builder.Services.AddScoped<ICategoryService, CategoryService>();
builder.Services.AddScoped<IDraftService, DraftService>();
builder.Services.AddScoped<IMealPlanService, MealPlanService>();
builder.Services.AddScoped<IShoppingListService, ShoppingListService>();
builder.Services.AddScoped<UnitConverter>();
builder.Services.AddScoped<IShoppingListGenerator, ShoppingListGenerator>();

// Collaboration services - singleton for cross-component communication
builder.Services.AddSingleton<DataNotifier>();
builder.Services.AddSingleton<PresenceService>();
builder.Services.AddSingleton<PollingService>();
builder.Services.AddHostedService<PollingService>(sp => sp.GetRequiredService<PollingService>());

// Site admin service - config-based admin role
builder.Services.AddSingleton<ISiteAdminService, SiteAdminService>();

// Recipe import services
builder.Services.AddSingleton<IUrlValidator, UrlValidator>();
builder.Services.AddScoped<IRecipeScraperService, RecipeScraperService>();
builder.Services.AddScoped<IRecipeImportService, RecipeImportService>();

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
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddMudServices();
builder.Services.AddBlazorDragDrop();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(options =>
    {
        options.MaximumReceiveMessageSize = 12 * 1024 * 1024; // 12 MB for file uploads
    });

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
// Clear KnownProxies/KnownNetworks to trust Docker network headers
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedHeadersOptions.KnownProxies.Clear();
forwardedHeadersOptions.KnownNetworks.Clear();
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

// First-run setup redirect middleware (BEFORE authorization)
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

    // Skip setup check for these paths (static assets, framework, auth, household request)
    if (path.StartsWith("/setup") ||
        path.StartsWith("/account") ||
        path.StartsWith("/household") ||
        path.StartsWith("/_framework") ||
        path.StartsWith("/_blazor") ||
        path.StartsWith("/_") ||
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
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Health check endpoint for Docker
app.MapGet("/health", () => Results.Ok("healthy"));

// Auth endpoints (minimal API) - wiring for Login.razor form POST
app.MapPost("/account/login-google", async (HttpContext context) =>
{
    // Get returnUrl from form, default to home
    var form = await context.Request.ReadFormAsync();
    var returnUrl = form["returnUrl"].FirstOrDefault() ?? "/";

    // Prevent open redirect - only allow local URLs
    if (!Uri.TryCreate(returnUrl, UriKind.Relative, out _) ||
        returnUrl.StartsWith("//") ||
        returnUrl.StartsWith("/\\"))
    {
        returnUrl = "/";
    }

    var properties = new AuthenticationProperties
    {
        RedirectUri = returnUrl,
        IsPersistent = true  // Remember me
    };
    await context.ChallengeAsync(GoogleDefaults.AuthenticationScheme, properties);
});

app.MapGet("/account/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/account/login");
});

app.Run();
