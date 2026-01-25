# Phase 1: Foundation & Infrastructure - Research

**Researched:** 2026-01-22
**Domain:** Blazor Server, PostgreSQL multi-tenancy, EF Core, Google OAuth, Docker deployment
**Confidence:** HIGH

## Summary

Phase 1 establishes a Blazor Server application with PostgreSQL database using composite keys for multi-tenant isolation, Google OAuth authentication with whitelist validation, and Docker Compose deployment configuration. The research confirms this is a well-established pattern with mature tooling and clear best practices.

**Key findings:**
- Blazor Server requires DbContextFactory pattern for thread-safe EF Core usage (not standard scoped DbContext)
- PostgreSQL composite foreign keys (HouseholdId + EntityId) provide database-level tenant isolation
- Google OAuth integration is straightforward via Microsoft.AspNetCore.Authentication.Google 10.0.1+
- Docker Compose with health checks ensures proper database readiness before application startup
- nginx reverse proxy requires specific WebSocket headers for Blazor Server SignalR connections

**Primary recommendation:** Use DbContextFactory with component-scoped DbContext instances, enforce composite keys at the database schema level (not just application code), and configure Forwarded Headers Middleware before other middleware when running behind nginx.

## Standard Stack

The established libraries/tools for this domain:

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| ASP.NET Core | 8.0+ / 10.0 | Web framework | Microsoft's modern web platform, mature Blazor Server support |
| Entity Framework Core | 8.0+ | ORM and migrations | First-party ORM with excellent PostgreSQL support |
| Npgsql.EntityFrameworkCore.PostgreSQL | 8.0+ | PostgreSQL provider | Official EF Core provider for PostgreSQL |
| Microsoft.AspNetCore.Authentication.Google | 10.0.1 | Google OAuth | Official Microsoft authentication provider |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| Microsoft.EntityFrameworkCore.Design | 8.0+ | Migration tooling | Required for `dotnet ef` CLI commands |
| Bogus | Latest | Fake data generation | Development seed data with realistic content |
| postgres (Docker image) | 17 | Database container | Official PostgreSQL Docker image |
| nginx (Docker image) | 1.27+ | Reverse proxy | Official nginx image for production deployment |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| DbContextFactory | Scoped DbContext | Factory required for thread safety in Blazor Server circuits |
| Composite FK constraints | Application-level filtering | Database constraints prevent data leaks at lowest level |
| Google OAuth | Azure AD / Auth0 | Google chosen per requirements; others add complexity |
| Docker Compose | Kubernetes | Compose simpler for single-server deployment ([SERVER]) |

**Installation:**
```bash
# Create new Blazor Server project
dotnet new blazorserver -n FamilyCoordinationApp

# Add required packages
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL
dotnet add package Microsoft.EntityFrameworkCore.Design
dotnet add package Microsoft.AspNetCore.Authentication.Google
dotnet add package Bogus  # Development only
```

## Architecture Patterns

### Recommended Project Structure
```
src/
├── FamilyCoordinationApp/
│   ├── Components/           # Razor components
│   │   ├── Pages/           # Routable pages
│   │   ├── Layout/          # Layout components
│   │   └── Shared/          # Reusable components
│   ├── Data/                # Database context and entities
│   │   ├── ApplicationDbContext.cs
│   │   ├── Entities/        # Entity classes
│   │   └── Configurations/  # EF Core entity configurations (Fluent API)
│   ├── Services/            # Business logic services
│   ├── Migrations/          # EF Core migrations
│   ├── Authorization/       # Custom authorization handlers/requirements
│   └── Program.cs
├── docker-compose.yml       # Production configuration
├── docker-compose.override.yml  # Development overrides
├── Dockerfile
├── .env.example            # Template for secrets (committed)
└── .env                    # Actual secrets (gitignored)
```

### Pattern 1: DbContextFactory with Component-Scoped DbContext
**What:** Create DbContext instances per operation or component lifetime, never inject DbContext directly as scoped service
**When to use:** All Blazor Server database operations (required for thread safety)
**Example:**
```csharp
// Source: https://learn.microsoft.com/en-us/aspnet/core/blazor/blazor-ef-core
// Registration in Program.cs
builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

// Component usage - short-lived operation
@inject IDbContextFactory<ApplicationDbContext> DbFactory

private async Task<List<Recipe>> LoadRecipesAsync()
{
    using var context = DbFactory.CreateDbContext();
    return await context.Recipes
        .Where(r => r.HouseholdId == currentHouseholdId)
        .ToListAsync();
}

// Component usage - component-scoped with change tracking
@implements IDisposable
@inject IDbContextFactory<ApplicationDbContext> DbFactory

private ApplicationDbContext Context { get; set; } = default!;

protected override void OnInitialized()
{
    Context = DbFactory.CreateDbContext();
}

public void Dispose() => Context?.Dispose();
```

### Pattern 2: Composite Primary and Foreign Keys for Multi-Tenancy
**What:** All tenant-owned entities use composite primary keys (HouseholdId, EntityId) with composite foreign keys
**When to use:** Every entity except User and Household tables (reference data)
**Example:**
```csharp
// Source: https://learn.microsoft.com/en-us/ef/core/modeling/keys
// Entity configuration
public class Recipe
{
    public int HouseholdId { get; set; }
    public int RecipeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ImagePath { get; set; } = string.Empty;

    // Navigation properties
    public Household Household { get; set; } = default!;
}

public class RecipeConfiguration : IEntityTypeConfiguration<Recipe>
{
    public void Configure(EntityTypeBuilder<Recipe> builder)
    {
        // Composite primary key
        builder.HasKey(r => new { r.HouseholdId, r.RecipeId });

        // Composite foreign key to Household
        builder.HasOne(r => r.Household)
            .WithMany(h => h.Recipes)
            .HasForeignKey(r => r.HouseholdId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

// Child entity with composite FK referencing parent composite PK
public class MealPlanRecipe
{
    public int HouseholdId { get; set; }
    public int MealPlanId { get; set; }
    public int RecipeId { get; set; }

    public MealPlan MealPlan { get; set; } = default!;
    public Recipe Recipe { get; set; } = default!;
}

public class MealPlanRecipeConfiguration : IEntityTypeConfiguration<MealPlanRecipe>
{
    public void Configure(EntityTypeBuilder<MealPlanRecipe> builder)
    {
        builder.HasKey(mpr => new { mpr.HouseholdId, mpr.MealPlanId, mpr.RecipeId });

        // Composite FK - order must match principal key order
        builder.HasOne(mpr => mpr.Recipe)
            .WithMany()
            .HasForeignKey(mpr => new { mpr.HouseholdId, mpr.RecipeId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```

### Pattern 3: Custom Authorization Policy with Email Whitelist
**What:** Custom authorization requirement and handler that validates user email against database whitelist
**When to use:** Every authenticated route (global policy)
**Example:**
```csharp
// Source: https://learn.microsoft.com/en-us/aspnet/core/security/authorization/policies
// Requirement
public class WhitelistedEmailRequirement : IAuthorizationRequirement { }

// Handler
public class WhitelistedEmailHandler : AuthorizationHandler<WhitelistedEmailRequirement>
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;

    public WhitelistedEmailHandler(IDbContextFactory<ApplicationDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        WhitelistedEmailRequirement requirement)
    {
        var emailClaim = context.User.FindFirst(ClaimTypes.Email);
        if (emailClaim is null)
        {
            return; // Fail authorization
        }

        using var dbContext = _dbFactory.CreateDbContext();
        var isWhitelisted = await dbContext.Users
            .AnyAsync(u => u.Email == emailClaim.Value);

        if (isWhitelisted)
        {
            context.Succeed(requirement);
        }
    }
}

// Registration in Program.cs
builder.Services.AddSingleton<IAuthorizationHandler, WhitelistedEmailHandler>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("WhitelistedOnly", policy =>
        policy.Requirements.Add(new WhitelistedEmailRequirement()));

    // Apply globally
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .AddRequirements(new WhitelistedEmailRequirement())
        .Build();
});
```

### Pattern 4: IAsyncDisposable for Component Cleanup
**What:** Implement IAsyncDisposable to properly dispose async resources (DbContext, timers, event subscriptions)
**When to use:** Components that create DbContext with component lifetime, subscribe to events, or use timers
**Example:**
```csharp
// Source: https://learn.microsoft.com/en-us/aspnet/core/blazor/components/component-disposal
@implements IAsyncDisposable
@inject IDbContextFactory<ApplicationDbContext> DbFactory

private ApplicationDbContext? Context { get; set; }

protected override void OnInitialized()
{
    Context = DbFactory.CreateDbContext();
}

public async ValueTask DisposeAsync()
{
    if (Context is not null)
    {
        await Context.DisposeAsync();
    }
}
```

### Pattern 5: Docker Compose with Health Checks for Database Readiness
**What:** Use depends_on with service_healthy condition to ensure PostgreSQL is ready before starting application
**When to use:** All Docker Compose configurations (dev and prod)
**Example:**
```yaml
# Source: https://docs.docker.com/compose/how-tos/startup-order/
version: '3.8'

services:
  postgres:
    image: postgres:17
    environment:
      POSTGRES_DB: familyapp
      POSTGRES_USER: ${POSTGRES_USER}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
    volumes:
      - postgres-data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U ${POSTGRES_USER}"]
      interval: 5s
      timeout: 5s
      retries: 5

  app:
    build: .
    depends_on:
      postgres:
        condition: service_healthy
    environment:
      ConnectionStrings__DefaultConnection: "Host=postgres;Database=familyapp;Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
      Authentication__Google__ClientId: ${GOOGLE_CLIENT_ID}
      Authentication__Google__ClientSecret: ${GOOGLE_CLIENT_SECRET}

volumes:
  postgres-data:
```

### Pattern 6: nginx Reverse Proxy with WebSocket Support
**What:** Configure nginx to proxy Blazor Server with WebSocket upgrade headers and forward client information
**When to use:** Production deployment behind reverse proxy
**Example:**
```nginx
# Source: https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/linux-nginx
server {
    listen 80;
    server_name your-domain.example.com;

    location / {
        proxy_pass http://app:8080;
        proxy_http_version 1.1;

        # WebSocket support (required for Blazor Server SignalR)
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;

        # Forward client information
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        # Disable buffering for real-time SignalR
        proxy_buffering off;
    }
}

# WebSocket connection upgrade mapping
map $http_upgrade $connection_upgrade {
    default upgrade;
    '' close;
}
```

### Pattern 7: Forwarded Headers Middleware Configuration
**What:** Configure ASP.NET Core to trust reverse proxy headers for correct scheme/host resolution
**When to use:** Any deployment behind reverse proxy (nginx, IIS, etc.)
**Example:**
```csharp
// Source: https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer
// Program.cs - must be called before other middleware
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// For production with known proxy IPs (more secure)
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedHeadersOptions.KnownProxies.Add(IPAddress.Parse("10.0.0.100")); // nginx container IP

app.UseForwardedHeaders(forwardedHeadersOptions);

// Must come before UseAuthentication, UseAuthorization
app.UseAuthentication();
app.UseAuthorization();
```

### Anti-Patterns to Avoid
- **Scoped DbContext in Blazor Server:** Causes thread safety issues due to long-lived circuits
- **Application-only tenant filtering:** Bypassed by raw SQL; use database constraints
- **Single Google OAuth client for dev/prod:** Complicates redirect URI management; use separate clients
- **depends_on without healthcheck:** App starts before database ready, causes connection failures
- **Storing DbContext in component field without disposal:** Memory leaks in long-running circuits
- **Not using DbContextFactory:** Concurrent operations corrupt DbContext state

## Don't Hand-Roll

Problems that look simple but have existing solutions:

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| OAuth authentication | Custom OAuth flow, token management | Microsoft.AspNetCore.Authentication.Google | Google's protocol is complex (token refresh, validation, security), library handles edge cases |
| Database migrations | Custom SQL scripts with version tracking | EF Core Migrations | Tracks schema changes, generates idempotent scripts, handles rollbacks |
| Multi-tenant isolation | WHERE clause filtering in queries | Composite FK constraints | Application filters can be bypassed; DB constraints are enforced at storage level |
| User session persistence | Custom cookie/token management | ASP.NET Core Authentication | Handles cookie encryption, expiration, renewal, anti-forgery |
| Data seeding | Manual INSERT scripts | EF Core HasData / UseSeeding | Integrates with migrations, handles updates/deletes, tracks changes |
| Secret management in Docker | Hardcoded values in compose file | .env file + environment variables | Prevents secrets in version control, easier rotation |
| Reverse proxy config | Custom proxy implementation | nginx official image | Battle-tested, handles edge cases (WebSockets, buffering, timeouts) |
| Component disposal | Manual cleanup in OnDisposed | IAsyncDisposable pattern | Framework integration, proper async support, prevents common mistakes |

**Key insight:** Infrastructure and security problems have hidden complexity. Even "simple" OAuth has edge cases around token expiration, refresh flows, and security vulnerabilities. Using mature libraries prevents entire classes of bugs.

## Common Pitfalls

### Pitfall 1: Using Scoped DbContext Instead of DbContextFactory
**What goes wrong:** `InvalidOperationException`: "A second operation started on this context before a previous operation completed"
**Why it happens:** Blazor Server circuits are long-lived (one per user session), and a scoped DbContext gets shared across multiple concurrent component operations within the circuit. DbContext is not thread-safe.
**How to avoid:** Always use `AddDbContextFactory` and create instances per operation (`using var context = factory.CreateDbContext()`)
**Warning signs:**
- Intermittent database errors under load
- Errors mentioning "concurrent operations"
- Works fine in testing (single user) but fails with multiple users

**References:**
- [ASP.NET Core Blazor with Entity Framework Core](https://learn.microsoft.com/en-us/aspnet/core/blazor/blazor-ef-core)
- [DbContext Lifetime, Configuration, and Initialization](https://learn.microsoft.com/en-us/ef/core/dbcontext-configuration/)

### Pitfall 2: Forgetting to Match Composite Key Property Order
**What goes wrong:** EF Core generates incorrect foreign key constraints, queries fail with "column does not exist" errors
**Why it happens:** EF Core requires the order of properties in `HasForeignKey(new { prop1, prop2 })` to exactly match the order in the principal key. PostgreSQL enforces this in the schema.
**How to avoid:** Define composite keys in a consistent order across all entities (e.g., always `HouseholdId` first), document the convention, verify generated migrations
**Warning signs:**
- Migration generates unexpected index names
- Foreign key constraint violations on valid data
- Queries generate SQL with columns in wrong order

**Reference:** [Foreign and principal keys in relationships - EF Core](https://learn.microsoft.com/en-us/ef/core/modeling/relationships/foreign-and-principal-keys)

### Pitfall 3: Not Implementing IAsyncDisposable for Component-Scoped DbContext
**What goes wrong:** Memory leaks in long-running circuits, DbContext instances never disposed until circuit terminates
**Why it happens:** Without `IAsyncDisposable`, the DbContext created in `OnInitialized` stays in memory. In Blazor Server, circuits can live for hours/days.
**How to avoid:** Always implement `IAsyncDisposable` when storing DbContext in component field, dispose in `DisposeAsync()`
**Warning signs:**
- Memory usage grows over time with active users
- Gen 2 garbage collection pressure
- Database connection pool exhaustion

**Reference:** [ASP.NET Core Razor component disposal](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/component-disposal)

### Pitfall 4: Missing Forwarded Headers Middleware Behind Reverse Proxy
**What goes wrong:** Redirect URIs use `http://` instead of `https://`, OAuth callbacks fail, authentication breaks
**Why it happens:** nginx terminates SSL and forwards HTTP to app. Without middleware, ASP.NET Core doesn't know the original scheme was HTTPS.
**How to avoid:** Call `app.UseForwardedHeaders()` before `UseAuthentication()` in Program.cs, configure nginx to send X-Forwarded-Proto header
**Warning signs:**
- OAuth redirect errors ("redirect_uri_mismatch")
- Mixed content warnings (HTTPS page loading HTTP resources)
- Authentication cookies not setting (SameSite/Secure issues)

**Reference:** [Configure ASP.NET Core to work with proxy servers and load balancers](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer)

### Pitfall 5: Using depends_on Without Health Check
**What goes wrong:** Application container starts before PostgreSQL is ready to accept connections, startup migration fails
**Why it happens:** `depends_on` only waits for container to start (process running), not for PostgreSQL to complete initialization and listen on port
**How to avoid:** Add `healthcheck` to postgres service, use `depends_on: postgres: condition: service_healthy`
**Warning signs:**
- "Connection refused" errors on first startup
- App works on restart but fails on initial docker-compose up
- Race condition in CI/CD pipelines

**References:**
- [Control startup order - Docker Compose](https://docs.docker.com/compose/how-tos/startup-order/)
- [Wait for Services to Start in Docker Compose](https://medium.com/@pavel.loginov.dev/wait-for-services-to-start-in-docker-compose-wait-for-it-vs-healthcheck-e0248f54962b)

### Pitfall 6: Hardcoding Secrets in docker-compose.yml
**What goes wrong:** Secrets committed to version control, exposed in git history, security breach
**Why it happens:** Convenience during development, not knowing about .env file support
**How to avoid:** Use `${VARIABLE}` syntax in docker-compose.yml, create .env file (gitignored), provide .env.example template
**Warning signs:**
- Passwords visible in compose file
- .env not in .gitignore
- CI/CD pipeline errors about missing environment variables

**Reference:** [Secrets - Docker Docs](https://docs.docker.com/reference/compose-file/secrets/)

### Pitfall 7: Not Configuring WebSocket Headers in nginx
**What goes wrong:** Blazor Server falls back to long polling (performance degradation), frequent disconnections, "Error: Failed to start the connection"
**Why it happens:** WebSocket requires HTTP/1.1 upgrade headers; nginx doesn't forward these by default
**How to avoid:** Set `proxy_http_version 1.1`, `proxy_set_header Upgrade $http_upgrade`, `proxy_set_header Connection $connection_upgrade`
**Warning signs:**
- Browser console shows "WebSocket connection failed"
- High network traffic (long polling fallback)
- SignalR reconnection attempts in logs

**Reference:** [How to Configure nginx as Reverse Proxy for Websocket](https://www.iaspnetcore.com/blog/blogpost/61328553c9a1551c3b8e5334)

### Pitfall 8: Storing Component State in Static Fields or Singletons
**What goes wrong:** State shared across all users, data from one user visible to others, security vulnerability
**Why it happens:** Misunderstanding Blazor Server architecture (each circuit is stateful per user, but runs on shared server)
**How to avoid:** Use scoped services for per-user state, never static fields, inject state services into components
**Warning signs:**
- User A sees User B's data
- State persists after logout
- Erratic behavior with multiple concurrent users

**References:**
- [Blazor Server Best Practices - Part 1](https://www.aradhaghi.com/blog/blazor-server-best-practices-part1)
- [10 Architecture Mistakes Developers Make in Blazor Projects](https://medium.com/dotnet-new/10-architecture-mistakes-developers-make-in-blazor-projects-and-how-to-fix-them-e99466006e0d)

## Code Examples

Verified patterns from official sources:

### Google OAuth Configuration
```csharp
// Source: https://learn.microsoft.com/en-us/aspnet/core/security/authentication/social/google-logins
// Program.cs
builder.Services.AddAuthentication()
    .AddGoogle(googleOptions =>
    {
        googleOptions.ClientId = builder.Configuration["Authentication:Google:ClientId"]!;
        googleOptions.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"]!;
    });

// appsettings.json structure (values in .env for Docker)
{
  "Authentication": {
    "Google": {
      "ClientId": "...",
      "ClientSecret": "..."
    }
  }
}

// Set secrets during development
// dotnet user-secrets set "Authentication:Google:ClientId" "YOUR_CLIENT_ID"
// dotnet user-secrets set "Authentication:Google:ClientSecret" "YOUR_CLIENT_SECRET"
```

### Data Seeding with UseSeeding (EF Core 9+)
```csharp
// Source: https://learn.microsoft.com/en-us/ef/core/modeling/data-seeding
// ApplicationDbContext.cs
protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
{
    base.OnConfiguring(optionsBuilder);

    if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development")
    {
        optionsBuilder.UseSeeding((context, _) =>
        {
            var db = (ApplicationDbContext)context;

            // Only seed if empty
            if (!db.Households.Any())
            {
                var household = new Household { Name = "Demo Household" };
                db.Households.Add(household);
                db.SaveChanges();

                // Seed recipes with realistic data using Bogus
                var recipeFaker = new Faker<Recipe>()
                    .RuleFor(r => r.HouseholdId, household.Id)
                    .RuleFor(r => r.Name, f => f.Lorem.Sentence(3))
                    .RuleFor(r => r.ImagePath, f => $"recipes/{f.Random.Guid()}.jpg");

                db.Recipes.AddRange(recipeFaker.Generate(20));
                db.SaveChanges();
            }
        });
    }
}
```

### Composite Key Entity Configuration
```csharp
// Source: https://learn.microsoft.com/en-us/ef/core/modeling/keys
// Data/Configurations/RecipeConfiguration.cs
public class RecipeConfiguration : IEntityTypeConfiguration<Recipe>
{
    public void Configure(EntityTypeBuilder<Recipe> builder)
    {
        builder.ToTable("Recipes");

        // Composite primary key (HouseholdId first by convention)
        builder.HasKey(r => new { r.HouseholdId, r.RecipeId });

        // Properties
        builder.Property(r => r.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(r => r.ImagePath)
            .HasMaxLength(500);

        // Relationships
        builder.HasOne(r => r.Household)
            .WithMany(h => h.Recipes)
            .HasForeignKey(r => r.HouseholdId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```

### Loading Flag Pattern for Concurrent Operation Prevention
```csharp
// Source: https://learn.microsoft.com/en-us/aspnet/core/blazor/blazor-ef-core
@inject IDbContextFactory<ApplicationDbContext> DbFactory

private bool Loading { get; set; }

private async Task DeleteRecipeAsync(int householdId, int recipeId)
{
    if (Loading) return;

    try
    {
        Loading = true;

        using var context = DbFactory.CreateDbContext();
        var recipe = await context.Recipes
            .FirstOrDefaultAsync(r => r.HouseholdId == householdId && r.RecipeId == recipeId);

        if (recipe != null)
        {
            context.Recipes.Remove(recipe);
            await context.SaveChangesAsync();
        }
    }
    finally
    {
        Loading = false;
    }
}
```

### PostgreSQL Volume Mounting (Docker Compose)
```yaml
# Source: https://hub.docker.com/_/postgres
# Note: PostgreSQL 18+ uses /var/lib/postgresql/18/docker for PGDATA
# This example uses PostgreSQL 17 (still current as of 2026)
services:
  postgres:
    image: postgres:17
    container_name: familyapp-postgres
    environment:
      POSTGRES_DB: ${POSTGRES_DB:-familyapp}
      POSTGRES_USER: ${POSTGRES_USER}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
    volumes:
      # For PostgreSQL 17 and below - mount at /var/lib/postgresql/data
      # NOT /var/lib/postgresql (that won't persist data)
      - /[ZFS_POOL]/docker-data/family-app/postgres:/var/lib/postgresql/data
    ports:
      - "5432:5432"
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U ${POSTGRES_USER}"]
      interval: 5s
      timeout: 5s
      retries: 5
    restart: unless-stopped
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Scoped DbContext | DbContextFactory | Blazor Server GA (2019) | Required for thread safety in circuits |
| HasData for all seeding | UseSeeding/UseAsyncSeeding | EF Core 9 (Nov 2024) | Better for complex/dynamic seed data |
| Manual dependency waiting | healthcheck conditions | Docker Compose v3.4 (2018) | Reliable service startup ordering |
| Single IDisposable | IAsyncDisposable support | Blazor .NET 5 (2020) | Proper async resource cleanup |
| Manual auth cookie management | AddAuthentication extensions | ASP.NET Core 2.0 (2017) | Simplified OAuth integration |
| Global KnownProxies (all IPs) | Explicit proxy IP configuration | Security best practice (2020+) | Prevents header spoofing attacks |

**Deprecated/outdated:**
- **Scoped DbContext in Blazor Server**: Replaced by DbContextFactory (causes concurrency errors)
- **EF Core's `EnsureCreated()` with migrations**: Don't mix; use either migrations OR EnsureCreated, not both
- **Unscoped `depends_on`**: Now supports conditions (service_healthy, service_completed_successfully)
- **ASPNETCORE_FORWARDEDHEADERS_ENABLED=true globally**: Only maps two headers; configure ForwardedHeadersOptions explicitly for production

## Open Questions

Things that couldn't be fully resolved:

1. **Recipe image storage format (relative paths vs filename only)**
   - What we know: Both approaches work; relative paths more portable if moving between storage backends
   - What's unclear: Best practice for Docker volume-mapped storage on specific ZFS path
   - Recommendation: Use relative paths from upload root (e.g., `recipes/{guid}.jpg`) to support future CDN/cloud storage migration without schema changes. Store base path in configuration.

2. **Post-logout redirect flow (landing page vs direct Google login)**
   - What we know: Can redirect to landing page (explains app purpose) or straight back to Google login
   - What's unclear: User experience preference for family app context
   - Recommendation: Start with landing page showing "You've been logged out" with prominent "Sign in again" button. Allows future addition of app description for non-users who stumble across the URL.

3. **Database migration timing (startup vs manual vs separate container)**
   - What we know: Three approaches all used in production - startup automatic, manual via `dotnet ef database update`, separate init container
   - What's unclear: Best approach for single-server deployment with restart scenarios
   - Recommendation: Use startup migration with `context.Database.Migrate()` in Program.cs for simplicity. Single server deployment means downtime is acceptable. Add lock/retry logic if scaling horizontally later.

4. **Google OAuth client separation (dev vs prod)**
   - What we know: Best practice is separate OAuth clients for different environments; not strictly required
   - What's unclear: Whether managing multiple redirect URIs in single client is problematic
   - Recommendation: Use separate OAuth clients (one for localhost:*, one for your-domain.example.com) to avoid accidental production auth with dev credentials. Minimal setup overhead.

5. **Docker Compose override usage (separate dev/prod configs)**
   - What we know: `docker-compose.override.yml` auto-loaded in dev, can use separate files for prod
   - What's unclear: Best practice for single-server deployment where dev and prod are same environment ([SERVER])
   - Recommendation: Use override pattern anyway - docker-compose.yml = production config, docker-compose.override.yml = development additions (volume mounts for hot reload, debug ports). Supports future multi-environment expansion.

## Sources

### Primary (HIGH confidence)
- [ASP.NET Core Blazor with Entity Framework Core](https://learn.microsoft.com/en-us/aspnet/core/blazor/blazor-ef-core?view=aspnetcore-10.0) - DbContextFactory patterns
- [DbContext Lifetime, Configuration, and Initialization](https://learn.microsoft.com/en-us/ef/core/dbcontext-configuration/) - Factory pattern and concurrency
- [Keys - EF Core](https://learn.microsoft.com/en-us/ef/core/modeling/keys) - Composite primary keys
- [Foreign and principal keys in relationships](https://learn.microsoft.com/en-us/ef/core/modeling/relationships/foreign-and-principal-keys) - Composite foreign keys
- [Google external login setup in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/social/google-logins?view=aspnetcore-10.0) - OAuth configuration
- [Policy-based authorization in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/policies?view=aspnetcore-9.0) - Custom authorization handlers
- [ASP.NET Core Razor component disposal](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/component-disposal?view=aspnetcore-9.0) - IAsyncDisposable patterns
- [Data Seeding - EF Core](https://learn.microsoft.com/en-us/ef/core/modeling/data-seeding) - UseSeeding vs HasData
- [Configure ASP.NET Core to work with proxy servers and load balancers](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0) - Forwarded Headers Middleware
- [Control startup order - Docker Compose](https://docs.docker.com/compose/how-tos/startup-order/) - Health check dependencies
- [postgres - Official Image - Docker Hub](https://hub.docker.com/_/postgres) - PostgreSQL Docker configuration
- [Persisting container data - Docker Docs](https://docs.docker.com/get-started/docker-concepts/running-containers/persisting-container-data/) - Volume persistence

### Secondary (MEDIUM confidence)
- [Ecto Multi-tenancy with foreign keys](https://hexdocs.pm/ecto/multi-tenancy-with-foreign-keys.html) - Composite FK patterns (different ORM, same database concept)
- [Multi-tenant data isolation with PostgreSQL Row Level Security](https://aws.amazon.com/blogs/database/multi-tenant-data-isolation-with-postgresql-row-level-security/) - Alternative isolation approaches
- [New Data Seeding Methods in Entity Framework Core 9](https://gavilan.blog/2024/11/22/new-data-seeding-methods-in-entity-framework-core-9/) - Modern seeding patterns
- [How to Configure nginx as Reverse Proxy for Websocket](https://www.iaspnetcore.com/blog/blogpost/61328553c9a1551c3b8e5334) - Blazor Server WebSocket config
- [Manage memory in deployed ASP.NET Core server-side Blazor apps](https://learn.microsoft.com/en-us/aspnet/core/blazor/host-and-deploy/server/memory-management?view=aspnetcore-9.0) - Circuit management

### Tertiary (LOW confidence - WebSearch only, marked for validation)
- [10 Architecture Mistakes Developers Make in Blazor Projects](https://medium.com/dotnet-new/10-architecture-mistakes-developers-make-in-blazor-projects-and-how-to-fix-them-e99466006e0d) - Common pitfalls (Medium article, Dec 2025)
- [Blazor Server Best Practices - Part 1](https://www.aradhaghi.com/blog/blazor-server-best-practices-part1) - Architecture patterns (blog post)
- [Complex Data Seeding in .NET Core with EF Core and Bogus](https://ziedrebhi.medium.com/complex-data-seeding-in-net-core-with-ef-core-and-bogus-22ed1485d6c9) - Seed data generation (Medium article)

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - Microsoft official packages, well-documented PostgreSQL provider
- Architecture patterns: HIGH - All patterns verified against official Microsoft documentation
- Composite keys: HIGH - Official EF Core and PostgreSQL documentation confirm approach
- Docker deployment: MEDIUM - Official Docker docs for compose/healthcheck, some community articles for Blazor-specific nginx config
- Pitfalls: MEDIUM-HIGH - Official docs for technical pitfalls, some community sources for architecture mistakes

**Research date:** 2026-01-22
**Valid until:** ~30 days (2026-02-21) - stack is mature and stable; patterns unlikely to change rapidly. Re-verify if ASP.NET Core 11 or EF Core 10 releases in this window.
