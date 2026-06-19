---
paths:
  - "src/FamilyCoordinationApp/**/*.cs"
  - "src/FamilyCoordinationApp/**/*.razor"
  - "tests/FamilyCoordinationApp.Tests/**/*.cs"
---

# Architecture — .NET / Blazor Server backend

*Path-scoped rule — auto-loads when you open app source (`src/FamilyCoordinationApp/**`) or tests. The load-bearing invariants (multi-tenant `HouseholdId` filtering, `IDbContextFactory`) are summarized always-on in `CLAUDE.md`; the full layout + patterns live here. Deployment / env config is a separate rule (`deployment.md`).*

**Stack**: .NET 10 / Blazor Server / PostgreSQL / MudBlazor / EF Core / Docker

## Project Layout

```
src/FamilyCoordinationApp/
  Program.cs              # Startup: DI, auth, middleware pipeline
  Data/
    ApplicationDbContext.cs
    Entities/              # EF entities (composite keys: HouseholdId + EntityId)
    Configurations/        # EF fluent config (IEntityTypeConfiguration<T>)
    SeedData.cs            # Dev seed data
  Services/
    Interfaces/            # Service contracts (IRecipeService, IMealPlanService, etc.)
    *Service.cs            # Business logic (scoped per-request)
  Components/
    Layout/                # MainLayout, NavMenu, ReconnectModal
    Pages/                 # Routable pages (Recipes, MealPlan, ShoppingList, Settings/*)
    Recipe/                # Recipe-specific components (RecipeCard, IngredientEntry, etc.)
    MealPlan/              # MealSlot, WeeklyCalendarView, WeeklyListView, RecipePickerDialog
    ShoppingList/          # ShoppingListItemRow, CategorySection, AddItemDialog, etc.
    Shared/                # Cross-cutting (UserAvatar, SyncStatusIndicator, FeedbackDialog)
  Authorization/           # WhitelistedEmailRequirement + handler
  Migrations/              # EF Core migrations
  Constants/               # CategoryDefaults
  Models/SchemaOrg/        # RecipeSchema POCO for JSON-LD import

tests/FamilyCoordinationApp.Tests/
  Services/                # Unit tests (InMemory EF provider)
  Security/                # Security-focused tests
```

## Key Architectural Patterns

**Multi-tenant isolation**: All entities use composite primary keys (`HouseholdId` + entity-specific ID). Every query filters by `HouseholdId`.

**DbContextFactory**: Blazor Server requires `IDbContextFactory<ApplicationDbContext>` (not `DbContext` injection) because SignalR circuits are long-lived and share the DI scope. Every service creates short-lived contexts via `dbFactory.CreateDbContextAsync()`.

**Collaboration infrastructure**: Three singletons coordinate multi-user state:
- `DataNotifier` — pub/sub for cross-component change notifications
- `PresenceService` — tracks online users via heartbeats
- `PollingService` — background service polling for changes from other users

**Authentication**: Google OAuth with cookie auth. Access controlled by email whitelist stored in the database (`WhitelistedEmailHandler`). Site admins configured via `SITE_ADMIN_EMAILS` env var.

**Recipe import pipeline**: `RecipeScraperService` (HTTP + AngleSharp for HTML parsing) → `RecipeImportService` (JSON-LD schema.org extraction) → `IngredientParser` (natural language parsing). Polly resilience on HTTP calls.

**Household connections**: Households can connect via invite codes to share recipes bidirectionally. `HouseholdConnectionService` manages invites, connections, and recipe copying.

## EF Core Migrations

Migrations auto-apply on startup via `context.Database.MigrateAsync()`. To add a new migration:

```bash
dotnet ef migrations add MigrationName --project src/FamilyCoordinationApp/FamilyCoordinationApp.csproj
```
