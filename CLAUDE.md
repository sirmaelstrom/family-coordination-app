# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Is

Family Coordination App — a household coordination app (meal planning, recipe management, shopping lists, chores, multi-user collaboration) on a self-hosted Ubuntu server, deployed via GitHub Actions. Originally Blazor Server; now a **Blazor Server shell hosting Svelte 5 islands** (strangler migration in progress). All 8 major interactive surfaces (chores, shopping-list, meal-plan, recipes, dashboard, settings A/B/C) ship as Svelte islands on `master`, each gated by a `*_USE_ISLAND` env flag (default `false` in `docker-compose.yml`, `true` in the local `.env`); the Blazor page is the fallback path. Island-host pattern + full flag table: `.claude/rules/architecture.md`.

**Status**: Production, actively used. The strangler is the largest in-flight thread — the final step, dropping Blazor Server's UI runtime entirely, is Spine keystone quest `ae67f7dc` (spiked on branch `spike/sveltekit-shell`, not yet merged). The separate `family-kitchen-svelte` project is dormant/superseded — it was never the migration vehicle; the in-repo `frontend/{island}` approach was.

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

# Build / watch a single Svelte island (name ∈ shopping-list, chores, meal-plan,
# recipes, dashboard, settings, connections, admin) — output → wwwroot/islands/<name>/
cd frontend/<name> && npm install && npm run build   # or: npm run dev
```

> No `.sln` file — build/test the `.csproj` directly (the commands above do).
> Docker build (MSB3552 workaround) + the prod deploy pipeline auto-load via `.claude/rules/deployment.md`. Adding an EF migration auto-loads via `.claude/rules/architecture.md`.

## Architecture (keystones)

**Stack**: .NET 10 / Blazor Server / PostgreSQL / MudBlazor / EF Core / Docker — **plus Svelte 5 + Vite islands** under `frontend/*` (strangler; see the island-host keystone below).

Load-bearing invariants — the full project layout + architectural patterns auto-load via `.claude/rules/architecture.md` when you open app source (`src/FamilyCoordinationApp/**`):
- **Multi-tenant isolation** — every entity has a composite key (`HouseholdId` + entity id) and **every query filters by `HouseholdId`**. It's a security boundary, not a convention.
- **DbContextFactory** — Blazor Server circuits are long-lived, so inject `IDbContextFactory<ApplicationDbContext>` (never `DbContext`) and create short-lived contexts via `dbFactory.CreateDbContextAsync()`.
- **Island-host pattern (strangler)** — `Components/Pages/*.razor` are now thin hosts that render a Svelte island when the surface's `*_USE_ISLAND` flag is on, else the original Blazor UI. Gate the fallback with the nested-conditional pattern or a long-lived Blazor component disposes in the prerender gap → `ObjectDisposedException` 500 (CORRECTIONS.md `fca-island-host-prerender-fallback-race`). Island source in `frontend/<name>/`; built assets in `wwwroot/islands/<name>/`.

Deployment + environment configuration detail auto-loads via `.claude/rules/deployment.md` when you open CI / Docker / deploy files.

## Roadmap

`.planning/ROADMAP.md` is the authoritative phase list + status; forward work is also in the Spine campaign **"Family Coordination App"** (`spine_map` for the live frontier — trust it over any snapshot here). As of 2026-06-25: core app Phases 1–7 and chores Phases 10–12 + 14 + 16 (Snooze) are shipped (8–9 deprecated); open phases are **13 — multi-room chores (M:N)** and **15 — equity rework / invisible labor**. Running in parallel and larger than either: the **strangler track** — the 8-island migration (see § What This Is) plus the de-Blazor keystone (Spine quest `ae67f7dc`) — which ROADMAP.md does not yet track as phases.

## Corrections
<!-- Also see global corrections: D:\Development\data\memory\CORRECTIONS.md -->

- [2026-04-16 UTC] PREPEND `sudo` to docker commands on the production host over SSH. Reason: docker socket not in user group.

<!-- /reflect completed 2026-04-16 UTC -->
