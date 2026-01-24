using System.Security.Claims;
using FamilyCoordinationApp.Authorization;
using FamilyCoordinationApp.Components;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Database configuration - DbContextFactory for Blazor Server thread safety
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Database=familyapp;Username=familyapp;Password=***REDACTED***";

builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

// Services
builder.Services.AddScoped<SetupService>();

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

    // Request email scope
    options.Scope.Add("email");
    options.Scope.Add("profile");
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
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAntiforgery();

// Auth middleware
app.UseAuthentication();

// First-run setup redirect middleware (BEFORE authorization)
app.Use(async (context, next) =>
{
    // Skip if an endpoint was already selected (static assets, etc.)
    var endpoint = context.GetEndpoint();
    if (endpoint != null)
    {
        await next();
        return;
    }

    var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

    // Skip setup check for these paths
    if (path.StartsWith("/setup") ||
        path.StartsWith("/account") ||
        path.StartsWith("/_framework") ||
        path.StartsWith("/_blazor") ||
        path.StartsWith("/_") ||
        path.StartsWith("/health") ||
        path.StartsWith("/lib") ||
        path.Contains("."))
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
