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
});

// Authorization
builder.Services.AddScoped<IAuthorizationHandler, WhitelistedEmailHandler>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("WhitelistedOnly", policy =>
        policy.Requirements.Add(new WhitelistedEmailRequirement()));

    // Apply whitelist check to all authenticated requests
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .AddRequirements(new WhitelistedEmailRequirement())
        .Build();
});

// Add services to the container.
builder.Services.AddHttpContextAccessor();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

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
app.UseAuthorization();

// First-run setup redirect middleware
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

    // Skip setup check for these paths
    if (path.StartsWith("/setup") ||
        path.StartsWith("/account") ||
        path.StartsWith("/_") ||
        path.StartsWith("/health") ||
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

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Health check endpoint for Docker
app.MapGet("/health", () => Results.Ok("healthy"));

// Auth endpoints (minimal API) - wiring for Login.razor form POST
app.MapPost("/account/login-google", async (HttpContext context) =>
{
    var properties = new AuthenticationProperties
    {
        RedirectUri = "/",
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
