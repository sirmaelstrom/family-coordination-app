# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Is

Family Coordination App — a household coordination app (meal planning, recipe management, shopping lists, chores, multi-user collaboration) on a self-hosted Ubuntu server, deployed via GitHub Actions. Originally Blazor Server; since the **de-Blazor flip (WP-12, 2026-07-04)** it is a **SvelteKit SPA (Svelte 5, `adapter-static`, `frontend/app/`) served at the site root by an ASP.NET Core backend** over `/api` with same-origin cookie auth. The only server-rendered UI is a small set of static Razor Pages (`src/FamilyCoordinationApp/Pages/`): login/legal/error + onboarding (request/pending/setup). Blazor Server, MudBlazor, the per-surface islands, and the `*_USE_ISLAND` flags are **gone**.

**Status**: Production, actively used. The strangler migration (8 islands → SvelteKit shell → flip) is complete — Spine keystone quest `ae67f7dc`. The separate `family-kitchen-svelte` project is dormant/superseded.

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

# Build / check the SvelteKit SPA (the app's entire UI) — output → frontend/app/build/,
# copied into wwwroot/ (site root) by the CopyAppSpa msbuild target
cd frontend/app && npm install && npm run check && npm run build   # or: npm run dev
```

> No `.sln` file — build/test the `.csproj` directly (the commands above do).
> Docker build (MSB3552 workaround) + the prod deploy pipeline auto-load via `.claude/rules/deployment.md`. Adding an EF migration auto-loads via `.claude/rules/architecture.md`.

## Architecture (keystones)

**Stack**: .NET 10 (ASP.NET Core: minimal-API `/api` + static Razor Pages) / PostgreSQL / EF Core / Docker — UI is a **SvelteKit SPA (Svelte 5)** in `frontend/app/`, served at the site root.

Load-bearing invariants — the full project layout + architectural patterns auto-load via `.claude/rules/architecture.md` when you open app source (`src/FamilyCoordinationApp/**`):
- **Multi-tenant isolation** — every entity has a composite key (`HouseholdId` + entity id) and **every query filters by `HouseholdId`**. It's a security boundary, not a convention. (Presence rosters are household-scoped too.)
- **DbContextFactory** — inject `IDbContextFactory<ApplicationDbContext>` (never `DbContext`) and create short-lived contexts via `dbFactory.CreateDbContextAsync()`.
- **SPA-at-root serving** — the SPA is served by EXPLICIT per-prefix `MapFallbackToFile` patterns in `Program.cs` (`/dashboard`, `/chores`, `/shopping-list`, `/meal-plan`, `/recipes`, `/settings`, `/`), NOT a broad catch-all (it would shadow the Razor Pages). Adding a new top-level SPA route requires adding its fallback prefix.
- **Non-empty /api error bodies** — any 4xx written on `/api` must carry a body, or `UseStatusCodePagesWithReExecute` re-executes it through the GET-only `/not-found` page and a non-GET call surfaces as 405 (CORRECTIONS `fca-empty-404-surfaces-as-405-on-delete`).
- **M8 session contract** — SPA routes read identity from the `$lib/session` store (`ctx()`), never per-route `/api/me` fetches; shared components live only in `$lib/shared/`.

Deployment + environment configuration detail auto-loads via `.claude/rules/deployment.md` when you open CI / Docker / deploy files.

## Roadmap

`.planning/ROADMAP.md` is the authoritative phase list + status; forward work is also in the Spine campaign **"Family Coordination App"** (`spine_map` for the live frontier — trust it over any snapshot here). As of 2026-07-04: core app Phases 1–7 and chores Phases 10–12 + 14 + 16 (Snooze) are shipped (8–9 deprecated); the **strangler/de-Blazor track is COMPLETE** (keystone `ae67f7dc` — 8 islands → SvelteKit shell → WP-12 flip). Open phases: **13 — multi-room chores (M:N)** and **15 — equity rework / invisible labor**.

## Corrections
<!-- Also see global corrections: D:\Development\data\memory\CORRECTIONS.md -->

- [2026-04-16 UTC] PREPEND `sudo` to docker commands on the production host over SSH. Reason: docker socket not in user group.

<!-- /reflect completed 2026-04-16 UTC -->
