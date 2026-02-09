# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Is

Family Coordination App — a Blazor Server application for household meal planning, recipe management, shopping lists, and multi-user collaboration. Deployed to a self-hosted Ubuntu server (homeserver) via GitHub Actions.

**Status**: Production, actively used. Remains the primary focus short-term. A SvelteKit rewrite (`family-kitchen-svelte`) is planned but not yet prioritized.

## Build & Test Commands

```bash
# Build
dotnet build src/FamilyCoordinationApp/FamilyCoordinationApp.csproj

# Run all tests (xUnit + FluentAssertions + Moq + Bogus)
dotnet test tests/FamilyCoordinationApp.Tests/FamilyCoordinationApp.Tests.csproj

# Run a single test
dotnet test tests/FamilyCoordinationApp.Tests/FamilyCoordinationApp.Tests.csproj --filter "FullyQualifiedName~IngredientParserTests.Parse_SimpleIngredient"

# Format check
dotnet format src/FamilyCoordinationApp/FamilyCoordinationApp.csproj --verify-no-changes

# Run locally (requires PostgreSQL + Google OAuth credentials)
dotnet run --project src/FamilyCoordinationApp/FamilyCoordinationApp.csproj
```

### Docker Build

There is a known .NET SDK 10.0 bug (MSB3552) that breaks multi-stage Docker builds. See `DOCKER-BUILD-WORKAROUND.md`. Use the workaround script:

```bash
./docker-build.sh          # Build with 'latest' tag
./docker-build.sh v1.0.0   # Build with specific tag
```

### EF Core Migrations

Migrations auto-apply on startup via `context.Database.MigrateAsync()`. To add a new migration:

```bash
dotnet ef migrations add MigrationName --project src/FamilyCoordinationApp/FamilyCoordinationApp.csproj
```

## Architecture

**Stack**: .NET 10 / Blazor Server / PostgreSQL / MudBlazor / EF Core / Docker

### Project Layout

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

### Key Architectural Patterns

**Multi-tenant isolation**: All entities use composite primary keys (`HouseholdId` + entity-specific ID). Every query filters by `HouseholdId`. There are no `.sln` files — build the `.csproj` directly.

**DbContextFactory**: Blazor Server requires `IDbContextFactory<ApplicationDbContext>` (not `DbContext` injection) because SignalR circuits are long-lived and share the DI scope. Every service creates short-lived contexts via `dbFactory.CreateDbContextAsync()`.

**Collaboration infrastructure**: Three singletons coordinate multi-user state:
- `DataNotifier` — pub/sub for cross-component change notifications
- `PresenceService` — tracks online users via heartbeats
- `PollingService` — background service polling for changes from other users

**Authentication**: Google OAuth with cookie auth. Access controlled by email whitelist stored in the database (`WhitelistedEmailHandler`). Site admins configured via `SITE_ADMIN_EMAILS` env var.

**Recipe import pipeline**: `RecipeScraperService` (HTTP + AngleSharp for HTML parsing) → `RecipeImportService` (JSON-LD schema.org extraction) → `IngredientParser` (natural language parsing). Polly resilience on HTTP calls.

**Household connections**: Households can connect via invite codes to share recipes bidirectionally. `HouseholdConnectionService` manages invites, connections, and recipe copying.

### Deployment

Push to `master` triggers GitHub Actions:
1. **CI** (`ci.yml`): Build, test, format check, Docker build validation (GitHub-hosted runners)
2. **Deploy** (`deploy.yml`): Self-hosted runner on homeserver pulls code, builds Docker image, deploys via `docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d`

Production traffic flow: `Internet → Cloudflare → homeserver:443 (host nginx) → localhost:8085 (app container)`

### Environment Configuration

Required env vars (set via `.env` on homeserver, generated from a secrets manager during deploy):
- `ConnectionStrings__DefaultConnection` — PostgreSQL connection string
- `Authentication__Google__ClientId` / `ClientSecret` — OAuth
- `SITE_ADMIN_EMAILS` — comma-separated admin emails
- `DATAPROTECTION_CERT` — optional base64 PFX for key encryption

## Remaining Planned Work

Phases 1-7 complete. Phases 8-9 were deprecated — the gaps are minor and not worth the effort given the app works well as-is.
