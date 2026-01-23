# Architecture Research

**Domain:** Family Meal Planning & Coordination
**Researched:** 2026-01-22
**Confidence:** HIGH

## Standard Architecture

### System Overview

```
┌─────────────────────────────────────────────────────────────────────┐
│                         UI LAYER (Blazor Components)                │
├─────────────────────────────────────────────────────────────────────┤
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐              │
│  │   Recipes    │  │  Meal Plan   │  │ Shopping     │              │
│  │  Components  │  │  Components  │  │    List      │              │
│  └──────┬───────┘  └──────┬───────┘  └──────┬───────┘              │
│         │                  │                  │                      │
├─────────┴──────────────────┴──────────────────┴──────────────────────┤
│                      SignalR Hub Layer                               │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │  MealPlanHub (broadcasts updates to all family members)      │   │
│  └──────────────────────────────────────────────────────────────┘   │
│         │                  │                  │                      │
├─────────┴──────────────────┴──────────────────┴──────────────────────┤
│                      APPLICATION LAYER (Services)                    │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐              │
│  │   Recipe     │  │  Meal Plan   │  │  Shopping    │              │
│  │   Service    │  │   Service    │  │    List      │              │
│  │              │  │              │  │   Service    │              │
│  └──────┬───────┘  └──────┬───────┘  └──────┬───────┘              │
│         │                  │                  │                      │
│         │  ┌───────────────┴──────────────┐   │                     │
│         │  │   Recipe Scraper Service     │   │                     │
│         │  │   (background/async)         │   │                     │
│         │  └──────────────────────────────┘   │                     │
│         │                  │                  │                      │
├─────────┴──────────────────┴──────────────────┴──────────────────────┤
│                      DATA LAYER (EF Core + PostgreSQL)               │
│  ┌────────────────────────────────────────────────────────────────┐  │
│  │              DbContextFactory (Blazor Server Pattern)          │  │
│  └────────────────────────────────────────────────────────────────┘  │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐              │
│  │   Recipes    │  │  Meal Plans  │  │  Shopping    │              │
│  │  Repository  │  │  Repository  │  │    Lists     │              │
│  └──────────────┘  └──────────────┘  └──────────────┘              │
│         │                  │                  │                      │
├─────────┴──────────────────┴──────────────────┴──────────────────────┤
│                      STORAGE                                         │
│  ┌──────────────┐                        ┌──────────────┐           │
│  │  PostgreSQL  │                        │ ZFS Storage  │           │
│  │  (metadata)  │                        │  (images)    │           │
│  └──────────────┘                        └──────────────┘           │
└─────────────────────────────────────────────────────────────────────┘
```

### Component Responsibilities

| Component | Responsibility | Typical Implementation |
|-----------|----------------|------------------------|
| **Blazor Components** | UI rendering, user input, drag-drop interactions | Razor components with code-behind |
| **SignalR Hub** | Real-time broadcast of state changes to connected clients | Strongly-typed hub with group membership |
| **Application Services** | Business logic, data orchestration, validation | Scoped services injected into components |
| **Recipe Scraper** | Asynchronous web scraping, HTML parsing, image download | Background service with HTTP client + parser |
| **Repositories** | Data access abstraction, LINQ query composition | EF Core with DbContextFactory pattern |
| **DbContext** | Entity tracking, change detection, database operations | PostgreSQL provider via Npgsql |

## Recommended Project Structure

```
FamilyMealPlanner/
├── FamilyMealPlanner.Server/           # Blazor Server host project
│   ├── Components/                     # Blazor components
│   │   ├── Features/                   # Feature-organized components
│   │   │   ├── Recipes/
│   │   │   │   ├── RecipeList.razor
│   │   │   │   ├── RecipeDetail.razor
│   │   │   │   ├── RecipeForm.razor
│   │   │   │   └── RecipeScraper.razor
│   │   │   ├── MealPlan/
│   │   │   │   ├── WeeklyPlanner.razor
│   │   │   │   ├── DayColumn.razor
│   │   │   │   └── RecipeCard.razor   # Draggable component
│   │   │   └── ShoppingList/
│   │   │       ├── ShoppingListView.razor
│   │   │       ├── IngredientGroup.razor
│   │   │       └── IngredientCheckbox.razor
│   │   └── Shared/                     # Reusable UI components
│   │       ├── MainLayout.razor
│   │       ├── NavMenu.razor
│   │       └── LoadingSpinner.razor
│   ├── Hubs/                           # SignalR hubs
│   │   └── MealPlanHub.cs             # Real-time collaboration hub
│   ├── wwwroot/                        # Static assets
│   │   ├── css/                        # Styles (mobile-first)
│   │   ├── js/                         # JS interop (drag-drop)
│   │   └── service-worker.js           # PWA offline support
│   ├── Program.cs                      # App configuration
│   └── appsettings.json                # Configuration
│
├── FamilyMealPlanner.Application/      # Business logic layer
│   ├── Services/
│   │   ├── RecipeService.cs
│   │   ├── MealPlanService.cs
│   │   ├── ShoppingListService.cs
│   │   └── RecipeScraperService.cs    # Web scraping logic
│   ├── DTOs/                           # Data transfer objects
│   └── Interfaces/                     # Service contracts
│
├── FamilyMealPlanner.Infrastructure/   # Data access layer
│   ├── Data/
│   │   ├── ApplicationDbContext.cs
│   │   ├── Migrations/
│   │   └── Configurations/             # EF Core entity configs
│   ├── Repositories/
│   │   ├── RecipeRepository.cs
│   │   ├── MealPlanRepository.cs
│   │   └── ShoppingListRepository.cs
│   └── FileStorage/
│       └── ImageStorageService.cs      # ZFS file operations
│
└── FamilyMealPlanner.Domain/           # Domain entities
    ├── Entities/
    │   ├── Recipe.cs
    │   ├── MealPlan.cs
    │   ├── ShoppingList.cs
    │   ├── Ingredient.cs
    │   └── Family.cs
    └── ValueObjects/
        ├── IngredientQuantity.cs
        └── MealSlot.cs
```

### Structure Rationale

- **Features/ over Pages/**: Organizes components by feature domain (recipes, meal planning, shopping) rather than technical role, improving discoverability and maintainability
- **Clean Architecture layers**: Separates concerns with Domain (entities), Infrastructure (data), Application (business logic), and Server (UI), enforcing dependency flow toward the domain
- **DbContextFactory pattern**: Required for Blazor Server to avoid sharing DbContext instances across concurrent operations within a user's circuit
- **SignalR Hub isolation**: Dedicated layer for real-time communication keeps broadcast logic separate from business services
- **PWA assets in wwwroot**: Service worker and manifest files enable offline-first mobile experience

## Architectural Patterns

### Pattern 1: DbContextFactory for Blazor Server

**What:** Use IDbContextFactory to create short-lived DbContext instances instead of injecting DbContext directly as a scoped service

**When to use:** Always in Blazor Server apps, especially with concurrent user interactions

**Trade-offs:**
- **Pros**: Thread-safe, avoids concurrency exceptions, proper context disposal
- **Cons**: Slightly more verbose (must dispose contexts manually)

**Example:**
```csharp
// Registration in Program.cs
builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

// Usage in service
public class RecipeService
{
    private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;

    public async Task<Recipe> GetRecipeAsync(int id)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Recipes
            .Include(r => r.Ingredients)
            .FirstOrDefaultAsync(r => r.Id == id);
    }
}
```

### Pattern 2: SignalR Groups for Family Isolation

**What:** Use SignalR groups to broadcast updates only to members of the same family, preventing cross-family data leakage

**When to use:** Multi-tenant meal planning where multiple families use the same app instance

**Trade-offs:**
- **Pros**: Secure isolation, efficient broadcasting, scalable
- **Cons**: Requires group membership management on connect/disconnect

**Example:**
```csharp
public class MealPlanHub : Hub
{
    public async Task JoinFamilyGroup(int familyId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"Family_{familyId}");
    }

    public async Task BroadcastMealPlanUpdate(int familyId, MealPlanUpdateDto update)
    {
        // Only send to members of this family
        await Clients.Group($"Family_{familyId}")
            .SendAsync("MealPlanUpdated", update);
    }
}
```

### Pattern 3: Component-Service-Repository Layering

**What:** Blazor components inject application services, services use repositories, repositories encapsulate EF Core queries

**When to use:** Standard pattern for all data-driven features

**Trade-offs:**
- **Pros**: Clear separation of concerns, testable layers, reusable logic
- **Cons**: More files/classes than directly using DbContext in components

**Example:**
```csharp
// Component layer
@inject IMealPlanService MealPlanService

private async Task OnRecipeDropped(int dayOfWeek, Recipe recipe)
{
    await MealPlanService.AssignRecipeToDay(CurrentWeek, dayOfWeek, recipe.Id);
    await MealPlanHub.BroadcastMealPlanUpdate(FamilyId, update);
}

// Service layer
public class MealPlanService : IMealPlanService
{
    private readonly IMealPlanRepository _repository;

    public async Task AssignRecipeToDay(DateTime week, int dayOfWeek, int recipeId)
    {
        var mealPlan = await _repository.GetOrCreateWeekPlan(week);
        mealPlan.AssignRecipe(dayOfWeek, recipeId);
        await _repository.SaveAsync(mealPlan);
    }
}

// Repository layer
public class MealPlanRepository : IMealPlanRepository
{
    private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;

    public async Task<MealPlan> GetOrCreateWeekPlan(DateTime week)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        // EF Core logic here
    }
}
```

### Pattern 4: Microservice-Style Scraper Isolation

**What:** Isolate web scraping into a separate service with queue-based processing to avoid blocking UI operations

**When to use:** Recipe URL scraping, which is slow and unreliable

**Trade-offs:**
- **Pros**: Non-blocking UI, retry logic, parallel scraping, failure isolation
- **Cons**: More complex than direct HTTP calls, requires background service infrastructure

**Example:**
```csharp
// Background service processing scrape jobs
public class RecipeScraperBackgroundService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _scrapeQueue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var recipe = await _scraper.ScrapeRecipeAsync(job.Url);
                await _recipeService.SaveScrapedRecipeAsync(recipe);
                await _hub.NotifyScraperComplete(job.FamilyId, recipe);
            }
            catch (Exception ex)
            {
                await _hub.NotifyScraperFailed(job.FamilyId, ex.Message);
            }
        }
    }
}
```

### Pattern 5: Aggregate Root for Shopping List Generation

**What:** MealPlan entity acts as aggregate root that can generate a shopping list by traversing all assigned recipes and aggregating ingredients

**When to use:** Shopping list generation from meal plan

**Trade-offs:**
- **Pros**: Business logic lives in domain model, easy to test, single source of truth
- **Cons**: Can become complex with unit conversions and ingredient matching

**Example:**
```csharp
// Domain entity
public class MealPlan
{
    public ICollection<MealSlot> Meals { get; set; }

    public ShoppingList GenerateShoppingList()
    {
        var ingredients = Meals
            .SelectMany(m => m.Recipe.Ingredients)
            .GroupBy(i => new { i.Name, i.Unit })
            .Select(g => new ShoppingListItem
            {
                Name = g.Key.Name,
                Quantity = g.Sum(i => i.Quantity),
                Unit = g.Key.Unit,
                Category = DetermineCategory(g.Key.Name)
            })
            .OrderBy(i => i.Category)
            .ThenBy(i => i.Name)
            .ToList();

        return new ShoppingList { Items = ingredients };
    }
}
```

## Data Flow

### Request Flow: Adding Recipe to Meal Plan

```
[User drags recipe to day column]
    ↓
[RecipeCard component] → OnDrop event handler
    ↓
[MealPlanService] → AssignRecipeToDay(week, day, recipeId)
    ↓
[MealPlanRepository] → EF Core query + SaveChanges
    ↓
[PostgreSQL] → INSERT/UPDATE
    ↓
[MealPlanService] → Success callback
    ↓
[MealPlanHub] → BroadcastMealPlanUpdate(familyId, update)
    ↓
[All connected family clients] → StateHasChanged() → UI refresh
```

### State Management: Real-Time Sync

```
[SignalR Hub]
    ↓ (Groups.AddToGroupAsync on connect)
[Components subscribe to hub events]
    ↓
[User A adds recipe] → Service → Hub.BroadcastMealPlanUpdate()
    ↓
[Hub sends to Group("Family_123")]
    ↓
[User B's component receives "MealPlanUpdated"]
    ↓
[Component updates local state] → StateHasChanged()
    ↓
[UI re-renders with new recipe]
```

### Key Data Flows

1. **Recipe Scraping Flow**: User submits URL → Scraper service queues job → Background worker fetches HTML → Parser extracts recipe data → Images saved to ZFS → Recipe saved to PostgreSQL → SignalR notifies completion
2. **Shopping List Generation Flow**: User clicks "Generate List" → MealPlan aggregate retrieves all assigned recipes → Ingredients aggregated by name/unit → Categorized (dairy, produce, meat, etc.) → Shopping list persisted → SignalR broadcasts to family
3. **Offline-First Sync (PWA)**: Service worker intercepts API calls → Checks IndexedDB cache → Returns cached data if offline → Queues mutations → Syncs when online → Resolves conflicts with server timestamp

## Scaling Considerations

| Scale | Architecture Adjustments |
|-------|--------------------------|
| 1-100 families | Single Blazor Server instance, PostgreSQL on same host, local ZFS storage. SignalR in-memory backplane sufficient. |
| 100-1,000 families | Multiple Blazor Server instances behind nginx load balancer. Redis backplane for SignalR state sharing. PostgreSQL on dedicated server. ZFS storage on NAS (already planned). |
| 1,000-10,000 families | Azure SignalR Service for managed scaling. PostgreSQL read replicas for query offload. CDN for recipe images. Background scraper workers scaled horizontally. |
| 10,000+ families | Azure SignalR Service + Azure Front Door. PostgreSQL sharding by family ID. Object storage (Azure Blob) for images. Distributed scraper fleet with queue (Azure Service Bus). |

### Scaling Priorities

1. **First bottleneck: SignalR connection limits** - Single server handles ~10K concurrent connections. Fix: Redis backplane for multi-instance deployment or Azure SignalR Service.
2. **Second bottleneck: Recipe scraping concurrency** - Sequential scraping is slow. Fix: Queue-based background workers with parallel scraping (rate-limited to avoid IP bans).
3. **Third bottleneck: Database write contention** - High update frequency on meal plans. Fix: Optimistic concurrency with row versioning, eventual consistency for non-critical updates.

## Anti-Patterns

### Anti-Pattern 1: Injecting DbContext as Scoped Service

**What people do:** Register `AddDbContext<ApplicationDbContext>()` and inject `ApplicationDbContext` directly into services

**Why it's wrong:** Blazor Server user circuits are long-lived scopes. Multiple concurrent operations (e.g., parallel SignalR calls) will share the same DbContext instance, causing "A second operation started on this context before a previous operation completed" errors.

**Do this instead:** Use `AddDbContextFactory<ApplicationDbContext>()` and inject `IDbContextFactory<ApplicationDbContext>`. Create short-lived contexts with `await using var context = await factory.CreateDbContextAsync();`

### Anti-Pattern 2: Storing Large State in Blazor Components

**What people do:** Load entire recipe database into a component's list and filter client-side

**Why it's wrong:** Blazor Server serializes component state and sends it over SignalR. Large state = slow rendering, high memory usage, network bottlenecks.

**Do this instead:** Use pagination, virtualization (`Virtualize` component), or server-side filtering. Only keep visible data in component state.

### Anti-Pattern 3: Synchronous Scraping in Request Handler

**What people do:** User clicks "Import Recipe" → Controller/service immediately scrapes URL → Returns after 5-10 seconds

**Why it's wrong:** Blocks Blazor circuit, poor UX, timeout risks, no retry logic for flaky websites.

**Do this instead:** Queue scrape job immediately, return job ID, use SignalR to notify completion. Show loading spinner and allow user to continue using app.

### Anti-Pattern 4: Broadcasting to All Clients Instead of Groups

**What people do:** `Clients.All.SendAsync("MealPlanUpdated", update)`

**Why it's wrong:** Sends updates to every connected user, exposing private family data across tenants.

**Do this instead:** Always use `Clients.Group($"Family_{familyId}").SendAsync()` after adding users to groups on connection.

### Anti-Pattern 5: Ignoring Drag-Drop Touch Support

**What people do:** Implement drag-drop using HTML5 Drag and Drop API only, which doesn't work on mobile browsers

**Why it's wrong:** HTML5 drag-drop has poor mobile support. Touch events need separate handling.

**Do this instead:** Use a Blazor component library (Telerik, Radzen) with built-in touch support, or implement both mouse (drag events) and touch (pointer events) handlers via JS interop.

## Integration Points

### External Services

| Service | Integration Pattern | Notes |
|---------|---------------------|-------|
| Recipe websites | HTTP scraping via background service | Use polite delays (1-2s between requests), respect robots.txt, cache aggressively. Microservice pattern with queue isolates failures. |
| PostgreSQL | EF Core with Npgsql provider | Use DbContextFactory. Migrations managed via `dotnet ef migrations`. Connection pooling enabled by default. |
| ZFS Storage | Direct file I/O via System.IO | Store images at `/zfs/meal-planner/images/{recipeId}/`. Serve via nginx static file handler for performance. |
| SignalR clients | Strongly-typed hub with auto-reconnect | Use `HubConnectionBuilder` with `WithAutomaticReconnect()`. Handle reconnection in Blazor components to reload state. |

### Internal Boundaries

| Boundary | Communication | Notes |
|----------|---------------|-------|
| Component ↔ Service | Direct DI injection | Components are stateful, services are scoped. Keep components thin (UI logic only), services fat (business logic). |
| Service ↔ Repository | Interface-based DI | Allows swapping implementations (e.g., mock repo for testing, cache layer). |
| Service ↔ SignalR Hub | IHubContext injection | Services can broadcast without being in hub context. Use typed hubs: `IHubContext<MealPlanHub, IMealPlanHubClient>`. |
| Background Service ↔ Services | Channel<T> for queues | Use `System.Threading.Channels` for in-process queuing (scraper jobs, email notifications). Bounded capacity to prevent memory leaks. |

## Mobile-First & PWA Considerations

### Progressive Web App Setup

Blazor Server apps can be PWA-enabled but have limitations:

1. **Service Worker Caching**: Cache static assets (CSS, JS, images) for offline UI shell. Cannot cache dynamic Blazor Server content (requires active WebSocket).
2. **Offline Fallback**: Show cached "You're offline" page when SignalR connection fails. Use service worker to detect network status.
3. **Manifest.json**: Configure for home screen installation with app name, icons, theme colors.

**Limitation**: Blazor Server requires active connection for interactivity. True offline functionality requires Blazor WebAssembly (out of scope for this project).

### Responsive Design Pattern

```
Mobile-first CSS approach:

/* Base styles for mobile (default) */
.recipe-card { width: 100%; }

/* Tablet and up */
@media (min-width: 768px) {
    .recipe-card { width: 50%; }
}

/* Desktop and up */
@media (min-width: 1024px) {
    .recipe-card { width: 33.33%; }
}
```

### Touch Optimization

- **Minimum touch target size**: 44x44px for all interactive elements (buttons, drag handles)
- **Drag-drop library requirement**: Must support touch events (e.g., Telerik UI, Radzen Blazor)
- **Swipe gestures**: Consider swipe-to-delete for shopping list items (requires JS interop)

## Build Order Implications

Based on dependency analysis, recommended build order:

1. **Phase 1: Data Layer Foundation**
   - Domain entities (Recipe, Ingredient, MealPlan, ShoppingList)
   - DbContext and migrations
   - Repository pattern implementation
   - **Rationale**: Everything depends on data access; build foundation first

2. **Phase 2: Recipe Management (Read-Only)**
   - Recipe CRUD services
   - Recipe Blazor components (list, detail, form)
   - Manual recipe entry (no scraping yet)
   - **Rationale**: Provides immediate value, no complex dependencies

3. **Phase 3: Meal Planning Core**
   - Meal plan service and repository
   - Weekly planner component with static cards
   - Assign recipes to days (click-based, no drag-drop yet)
   - **Rationale**: Core workflow, simpler before adding drag-drop and real-time

4. **Phase 4: Shopping List Generation**
   - Shopping list service with ingredient aggregation
   - Shopping list component with categorization
   - Generate from meal plan logic
   - **Rationale**: Depends on meal plan existing, no external dependencies

5. **Phase 5: Real-Time Collaboration (SignalR)**
   - MealPlanHub implementation
   - SignalR client integration in components
   - Family group management
   - **Rationale**: Enhancement to existing features, complex but isolated

6. **Phase 6: Drag-Drop UX**
   - JS interop for drag-drop (or component library integration)
   - Enhance meal planner with drag-drop
   - **Rationale**: UX polish, depends on meal plan existing

7. **Phase 7: Recipe Scraping**
   - Background scraper service
   - HTML parser for common recipe sites
   - Queue-based job processing
   - Image download and storage
   - **Rationale**: Most complex, highest failure risk, builds on recipes existing

8. **Phase 8: PWA & Mobile Optimization**
   - Service worker configuration
   - Responsive CSS refinement
   - Touch gesture support
   - Offline fallback page
   - **Rationale**: Polish phase, requires stable core features

## Sources

### Blazor Server Architecture
- [Blazor Best Practices for TOP Architecture and Performance](https://blog.devart.com/asp-net-core-blazor-best-practices-architecture-and-performance-optimization.html)
- [Architectural Patterns in Blazor](https://inspeerity.com/blog/architectural-patterns-in-blazor/)
- [Clean Architecture With Blazor Server - Enterprise Application Template](https://architecture.blazorserver.com/)
- [ASP.NET Core Blazor performance best practices - Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/blazor/performance/?view=aspnetcore-10.0)
- [Project structure for Blazor apps - Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/architecture/blazor-for-web-forms-developers/project-structure)

### SignalR Real-Time Architecture
- [Adding Real-Time Functionality To .NET Applications With SignalR](https://www.milanjovanovic.tech/blog/adding-real-time-functionality-to-dotnet-applications-with-signalr)
- [Overview of ASP.NET Core SignalR - Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/signalr/introduction?view=aspnetcore-10.0)
- [SignalR Explained: How SignalR works, limitations & use cases](https://ably.com/topic/signalr-deep-dive)

### Recipe Scraping Architecture
- [Web Scraping Infrastructure That Doesn't Break Under Pressure](https://groupbwt.com/blog/infrastructure-of-web-scraping/)
- [Setting up a large-scale scraping architecture with python - Medium](https://sia-ai.medium.com/setting-up-a-large-scale-scraping-architecture-with-python-3b26cb6571a6)
- [State of Web Scraping 2026: Trends, Challenges & What's Next](https://www.browserless.io/blog/state-of-web-scraping-2026)

### PostgreSQL + EF Core Integration
- [Step-by-Step Guide: Build a CRUD Blazor App with Entity Framework and PostgreSQL](https://dev.to/auyeungdavid_2847435260/step-by-step-guide-build-a-crud-blazor-app-with-entity-framework-and-postgresql-3ccj)
- [ASP.NET Core Blazor with Entity Framework Core (EF Core) - Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/blazor/blazor-ef-core?view=aspnetcore-8.0)
- [How to Configure PostgreSQL in Entity Framework Core - Code Maze](https://code-maze.com/configure-postgresql-ef-core/)

### Drag-Drop UI Patterns
- [Blazor Basics: Building Drag-and-Drop Functionality](https://www.telerik.com/blogs/blazor-basics-building-drag-drop-functionality-blazor-applications)
- [Blazor Drag and Drop - Radzen](https://www.radzen.com/blog/blazor-drag-and-drop)
- [Investigating Drag and Drop with Blazor - Chris Sainty](https://chrissainty.com/investigating-drag-and-drop-with-blazor/)

### PWA & Mobile Patterns
- [Offline-First Strategy with Blazor PWAs: A Complete Guide - Medium](https://medium.com/@dgallivan23/offline-first-strategy-with-blazor-pwas-a-complete-guide-a6e27e564d0c)
- [ASP.NET Core Blazor Progressive Web Application (PWA) - Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/blazor/progressive-web-app/?view=aspnetcore-9.0)
- [From Concept to Deployment: Real-World Case Study of a Blazor PWA - Medium](https://medium.com/@dgallivan23/from-concept-to-deployment-real-world-case-study-of-a-blazor-pwa-fa20ef56b2eb)

### Meal Planning Application Architecture
- [How to Develop a Diet and Nutrition App: Plan, Techs, Costs](https://www.scnsoft.com/healthcare/mobile/diet-nutrition-apps)
- [Meal Planning App Development: Comprehensive Guide](https://www.wdptechnologies.com/meal-planning-app-development/)

---
*Architecture research for: Family Meal Planning & Coordination*
*Researched: 2026-01-22*
