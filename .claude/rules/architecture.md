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
  src/lib/api/             #   M9 wire-contract pin: shape.ts + contracts.ts + contracts.test.ts
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

**SPA serving (Program.cs)**: the SvelteKit build is copied into `wwwroot/` (CopyAppSpa target locally, Docker stage in prod) and served by EXPLICIT per-prefix `MapFallbackToFile("<prefix>/{**slug}", "index.html")` patterns — `/dashboard`, `/chores`, `/shopping-list`, `/meal-plan`, `/recipes`, `/settings`, plus `/`. Never a broad root catch-all: it would shadow the Razor Pages (`/account/*`, `/household/*`, `/setup`, legal, error) and turn unknown URLs into silent SPA loads. **Adding a new top-level SPA route requires adding its fallback prefix.** (The `/shoppinglists` → `/shopping-list` 301 shim for pre-flip bookmarks was removed in the A11 sweep; that URL now 404s.)

**Auth**: Google OAuth challenge + 30-day sliding cookie (`FamilyApp.Auth`, SameSite=Lax), whitelist policy (`WhitelistedEmailHandler`). `/api` auth failures surface as bare 401/403 **with a JSON body** via `ApiAwareAuthEvents` (wired to Google + cookie schemes) — never a 302. Site admins via `SITE_ADMIN_EMAILS` env var.

**Non-empty /api 4xx (pipeline-enforced since PR #90)**: `/api` is branched via `UseWhen` past `UseStatusCodePagesWithReExecute("/not-found")` to `UseStatusCodePages`, which writes a JSON `{message}` (`ApiStatusMessages.For`) on any error response that wrote no body, leaving the status intact; non-`/api` keeps the re-execute page. The calendar feed's capability 404 is the deliberate no-oracle exception and stays empty. This covers routing (404/405) and the auth pipeline as well as handler returns, so the ~126 bodiless `Results.NotFound()`/`Results.Unauthorized()` call sites are safe without being edited — but a handler that knows why it failed should still say so. Guard: `Integration/ApiErrorBodyTests`. **Measured before the fix:** a POST to a GET-only `/api` route produced a routing 405 that re-execution rewrote to an empty 400. **The exception half is closed too** (quest `ea816df2`): an `/api` `UseWhen` branch wraps the pipeline with `UseExceptionHandler` → `ApiExceptionResponse.Write`, so a thrown `BadHttpRequestException` (Development's `ThrowOnBadRequest` bind failures — these used to answer a text/plain stack trace) keeps its own status and any other unhandled throw answers a JSON 500 instead of re-executing the HTML /Error page onto an /api caller. Measured at pickup: outside Development a failed bind does NOT throw — it is a bodiless 400 the backfill already covered.

**Dev-auth bypass (Development only)**: `DevAuthBypassMiddleware` injects a config/first-DB-user identity for anonymous requests — registration is env-gated, the middleware re-checks `IsDevelopment()`, and `DevAuthStartupGuard` fail-closes startup if `DEV_AUTH_BYPASS` is set outside Development.

**Session contract (M8)**: SPA routes read identity from the canonical `$lib/session` store and build a `ShellContext` via `ctx()` — never per-route `/api/me` fetches. Shared components live only in `$lib/shared/`. Presence decay (Online→Away→Offline) is read-driven: `GET /api/presence/users` runs `PresenceService.UpdatePresence()` before reading.

**Wire contract (M9 pin v2), both halves pinned**: each DTO **that has a checked-in JSON fixture** under `tests/FamilyCoordinationApp.Tests/Fixtures/` — responses AND the write bodies `RecipeWriteRequest`/`SaveDraftRequest`/`CategoryWriteRequest` — is serialized and compared to it by a `*ContractTests` class. The SPA half is pinned in `$lib/api`: `contracts.ts` declares each pinned island type in `PinnedTypes` and holds its `Shape` to it via the invariant `SHAPES` manifest under `npm run check` (a pin structurally cannot exist without its compile-time assertion), and `contracts.test.ts` validates each fixture against its Shape under `npm test`. **A field rename must therefore land in the DTO, the fixture AND the island's `types.ts`** — updating only the first two leaves TS type-checking TS and the field arrives `undefined` at runtime, which is the gap this closes. Undeclared and missing keys both fail; a new JSON fixture must gain a `PinnedTypes`+`SHAPES`+`PIN_FIXTURES` entry or be named in `SERVER_ONLY_FIXTURES`, or the coverage test fails. **Enum vocabularies are pinned by list-equality**: `WireEnumContractTests` byte-compares every wire enum's `Enum.GetValues` (camelCase for enum-typed fields, `ToString()` PascalCase for `recurrenceMode`/`effortTier`) to `Fixtures/Enums/wire-enums.json`, and `contracts.test.ts` holds the SPA's `WIRE_ENUMS` lists to that fixture — so a new C# enum member cannot reach the wire unannounced. Still a fixture-driven guard, not complete coverage: a payload without a C# fixture (e.g. the tri-state digest-settings update body) is unpinned until one is checked in. Not pinnable as enums: `callerCapacityTier` (string column, no enum — A9) and the goneQuiet/ghost `reason` (documented string field).

**Recipe import pipeline**: `RecipeScraperService` (HTTP + AngleSharp) → `RecipeImportService` (JSON-LD schema.org) → `IngredientParser`. Polly resilience on HTTP calls.

**Household connections**: Households connect via invite codes to share recipes bidirectionally (`HouseholdConnectionService`).

## EF Core Migrations

Migrations auto-apply **at startup, in every environment**, from the unconditional block in `Program.cs` right
after `builder.Build()` — and that block is the **only** migrator there is: no `dotnet ef` step exists in the
Dockerfile, docker-compose or CI. Do not re-gate it on `IsDevelopment()`. Until PR #93 it *was* dev-gated, which
left `SetupService.IsSetupCompleteAsync`'s per-call `Database.MigrateAsync()` as production's real migrator — the
schema landed on whichever request arrived first after a deploy, and every request thereafter re-took an
`ACCESS EXCLUSIVE` lock on `__EFMigrationsHistory` (measured on the local stack: 10 requests to `/api/me` → 100
history statements + 20 exclusive locks; after the move, 0). Guard: `Integration/StartupMigrationTests`.

Two consequences worth knowing: a database that is unreachable at boot now fails startup instead of degrading into
a `/setup` redirect loop (compose gates `app` on a healthy `postgres` and restarts it), and `IsSetupCompleteAsync`
is a pure read latched by the singleton `SetupCompletionLatch` once a household is observed.

To add a new migration:

```bash
dotnet ef migrations add MigrationName --project src/FamilyCoordinationApp/FamilyCoordinationApp.csproj
```
