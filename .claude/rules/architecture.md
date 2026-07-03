---
paths:
  - "src/FamilyCoordinationApp/**/*.cs"
  - "src/FamilyCoordinationApp/**/*.razor"
  - "tests/FamilyCoordinationApp.Tests/**/*.cs"
---

# Architecture — .NET / Blazor Server backend

*Path-scoped rule — auto-loads when you open app source (`src/FamilyCoordinationApp/**`) or tests. The load-bearing invariants (multi-tenant `HouseholdId` filtering, `IDbContextFactory`) are summarized always-on in `CLAUDE.md`; the full layout + patterns live here. Deployment / env config is a separate rule (`deployment.md`).*

**Stack**: .NET 10 / Blazor Server / PostgreSQL / MudBlazor / EF Core / Docker — **plus Svelte 5 + Vite islands** under `frontend/*` (strangler in progress; see § Strangler / Svelte Islands). Anyone editing `Components/Pages/*.razor` today is editing a thin island *host*, not the primary UI — that's now the fallback path.

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

## Strangler / Svelte Islands

The UI is mid-strangler: Blazor Server is now a **shell** hosting Svelte 5 islands. All 8 major interactive surfaces have shipped as islands on `master`, each behind a `*_USE_ISLAND` env flag (default `false` in `docker-compose.yml`, `true` in local `.env`). Flag on → the `.razor` page renders the island; flag off → the original Blazor UI is the fallback.

| Surface | Flag | Island source | Blazor host |
|---|---|---|---|
| Shopping list | `SHOPPING_LIST_USE_ISLAND` | `frontend/shopping-list/` | `ShoppingList.razor` |
| Chores | `CHORES_USE_ISLAND` | `frontend/chores/` | `Chores.razor` |
| Meal plan | `MEAL_PLAN_USE_ISLAND` | `frontend/meal-plan/` | `MealPlan.razor` |
| Recipes | `RECIPES_USE_ISLAND` | `frontend/recipes/` | `Recipes.razor` |
| Dashboard / home | `DASHBOARD_USE_ISLAND` | `frontend/dashboard/` | `Home.razor` |
| Settings A (categories, users) | `SETTINGS_HOUSEHOLD_USE_ISLAND` | `frontend/settings/` | `Categories.razor`, `WhitelistAdmin.razor` |
| Settings B (connections) | `SETTINGS_CONNECTIONS_USE_ISLAND` | `frontend/connections/` | `Connections.razor` |
| Settings C (admin) | `SETTINGS_ADMIN_USE_ISLAND` | `frontend/admin/` | `FeedbackAdmin.razor`, `HouseholdAdmin.razor` |

**Island-host gate (load-bearing):** gate the Blazor fallback with the *nested*-conditional pattern (outer `@if (_useIsland)` chooses island-path vs fallback; inner `@if (_shell is not null)` gates the actual island render) — NOT a single `_useIsland && _shell` condition, which lets a long-lived-context Blazor fallback mount-then-dispose in the prerender gap → `ObjectDisposedException` 500. Backend tests can't catch it; only a live `:8080` browser check does. Full detail: CORRECTIONS.md `fca-island-host-prerender-fallback-race`.

**Build:** each island is an independent Vite build (`frontend/<name>/`, `npm run build`) → `wwwroot/islands/<name>/`. In Docker, one `node:20-alpine` stage per island (see `.claude/rules/deployment.md`). Remaining Blazor-only surfaces (auth/setup/static, reasonably out of scope): Login, Landing, Household/Pending+Request, Setup/FirstRunSetup, Privacy, Terms, Error, NotFound.

**Full de-Blazor (Option A):** an unmerged spike on branch `spike/sveltekit-shell` (`frontend/app/`, adapter-static SvelteKit SPA served over `/api`, `base=/app`) keeps the .NET backend but drops Blazor Server's UI runtime entirely. Spine keystone quest `ae67f7dc`; commit message flags it "not wired for a prod flip."

## EF Core Migrations

Migrations auto-apply on startup via `context.Database.MigrateAsync()`. To add a new migration:

```bash
dotnet ef migrations add MigrationName --project src/FamilyCoordinationApp/FamilyCoordinationApp.csproj
```
