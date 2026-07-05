---
paths:
  - "src/FamilyCoordinationApp/**/*.cs"
  - "src/FamilyCoordinationApp/**/*.cshtml"
  - "frontend/app/src/**"
  - "tests/FamilyCoordinationApp.Tests/**/*.cs"
---

# Architecture — .NET API + SvelteKit SPA

*Path-scoped rule — auto-loads when you open app source (`src/FamilyCoordinationApp/**`, `frontend/app/src/**`) or tests. The load-bearing invariants (multi-tenant `HouseholdId` filtering, `IDbContextFactory`, per-prefix SPA fallbacks, non-empty /api error bodies) are summarized always-on in `CLAUDE.md`; the full layout + patterns live here. Deployment / env config is a separate rule (`deployment.md`).*

**Stack**: .NET 10 (ASP.NET Core minimal-API `/api` + static Razor Pages) / PostgreSQL / EF Core / Docker — UI is a **SvelteKit SPA (Svelte 5, `adapter-static`)** in `frontend/app/`, served at the site root. Blazor Server, MudBlazor, and the per-surface islands were removed in the de-Blazor flip (WP-12, 2026-07-04, keystone quest `ae67f7dc`).

## Project Layout

```
src/FamilyCoordinationApp/
  Program.cs              # Startup: DI, auth, middleware pipeline, endpoint maps, SPA fallbacks
  Data/
    ApplicationDbContext.cs
    Entities/              # EF entities (composite keys: HouseholdId + EntityId)
    Configurations/        # EF fluent config (IEntityTypeConfiguration<T>)
    SeedData.cs            # Dev seed data
  Services/
    Interfaces/            # Service contracts (IRecipeService, IMealPlanService, etc.)
    *Service.cs            # Business logic (scoped per-request; PresenceService is a singleton)
  Endpoints/               # Minimal-API groups: Me, Presence, ShoppingList, Chores, Rooms,
                           # MealPlan, Recipes, Dashboard, Settings{,Connections,Admin}
  Pages/                   # Static Razor Pages (the only server-rendered UI):
    Account/               #   Login, AccessDenied
    Household/             #   Request, Pending (onboarding, antiforgery-validated OnPost)
    Setup/                 #   FirstRunSetup
    Shared/_Layout.cshtml  #   self-contained theme-aware layout (no external CSS deps)
    Error, NotFound, Privacy, Terms
  Authorization/           # WhitelistedEmailRequirement + handler, DevAuthBypassMiddleware,
                           # DevAuthStartupGuard, ApiAwareAuthEvents (/api 401/403-with-body)
  Migrations/              # EF Core migrations
  Constants/               # CategoryDefaults
  Models/SchemaOrg/        # RecipeSchema POCO for JSON-LD import

frontend/app/              # The SvelteKit SPA (the app's entire UI)
  src/routes/              #   dashboard, chores, shopping-list[/listId], meal-plan,
                           #   recipes{,/new,/import,/edit/[id]}, settings/{5 pages}
  src/lib/session.svelte.ts  # canonical session store — routes build ctx() from it (M8)
  src/lib/presence.svelte.ts # 30s heartbeat + roster poller (401/403 → stop + redirect)
  src/lib/shell/           #   Header/Nav/MobileBottomNav/Footer
  src/lib/shared/          #   ConfirmDialog, PromptDialog, Toasts, avatars, toast-store
  src/lib/<surface>/       #   per-surface app + stores (chores, meal-plan, recipes, …)
  static/                  #   manifest.json + service-worker.js (root-scoped PWA)

tests/FamilyCoordinationApp.Tests/
  Services/                # Unit tests (InMemory EF provider)
  Security/                # Security-focused tests
  Authorization/           # Dev-auth bypass + ApiAwareAuthEvents tests
  Integration/             # Testcontainers (anon /api/me → 401 not 302)
```

## Key Architectural Patterns

**Multi-tenant isolation**: All entities use composite primary keys (`HouseholdId` + entity-specific ID). Every query filters by `HouseholdId` — it's a security boundary. This includes the in-memory presence roster (`PresenceService.GetAllActiveUsers(householdId)`).

**DbContextFactory**: Services and endpoints inject `IDbContextFactory<ApplicationDbContext>` (not `DbContext`) and create short-lived contexts via `dbFactory.CreateDbContextAsync()`.

**SPA serving (Program.cs)**: the SvelteKit build is copied into `wwwroot/` (CopyAppSpa target locally, Docker stage in prod) and served by EXPLICIT per-prefix `MapFallbackToFile("<prefix>/{**slug}", "index.html")` patterns — `/dashboard`, `/chores`, `/shopping-list`, `/meal-plan`, `/recipes`, `/settings`, plus `/`. Never a broad root catch-all: it would shadow the Razor Pages (`/account/*`, `/household/*`, `/setup`, legal, error) and turn unknown URLs into silent SPA loads. **Adding a new top-level SPA route requires adding its fallback prefix.** `/shoppinglists` (old Blazor route) 301s to `/shopping-list`.

**Auth**: Google OAuth challenge + 30-day sliding cookie (`FamilyApp.Auth`, SameSite=Lax), whitelist policy (`WhitelistedEmailHandler`). `/api` auth failures surface as bare 401/403 **with a JSON body** via `ApiAwareAuthEvents` (wired to Google + cookie schemes) — never a 302, and never an empty body (an empty 4xx re-executes through the GET-only `/not-found` page → non-GET calls become 405; CORRECTIONS `fca-empty-404-surfaces-as-405-on-delete`). Site admins via `SITE_ADMIN_EMAILS` env var.

**Dev-auth bypass (Development only)**: `DevAuthBypassMiddleware` injects a config/first-DB-user identity for anonymous requests — registration is env-gated, the middleware re-checks `IsDevelopment()`, and `DevAuthStartupGuard` fail-closes startup if `DEV_AUTH_BYPASS` is set outside Development.

**Session contract (M8)**: SPA routes read identity from the canonical `$lib/session` store and build a `ShellContext` via `ctx()` — never per-route `/api/me` fetches. Shared components live only in `$lib/shared/`. Presence decay (Online→Away→Offline) is read-driven: `GET /api/presence/users` runs `PresenceService.UpdatePresence()` before reading.

**Recipe import pipeline**: `RecipeScraperService` (HTTP + AngleSharp) → `RecipeImportService` (JSON-LD schema.org) → `IngredientParser`. Polly resilience on HTTP calls.

**Household connections**: Households connect via invite codes to share recipes bidirectionally (`HouseholdConnectionService`).

## EF Core Migrations

Migrations auto-apply on startup via `context.Database.MigrateAsync()`. To add a new migration:

```bash
dotnet ef migrations add MigrationName --project src/FamilyCoordinationApp/FamilyCoordinationApp.csproj
```
