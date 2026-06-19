# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Is

Family Coordination App — a Blazor Server application for household meal planning, recipe management, shopping lists, and multi-user collaboration. Deployed to a self-hosted Ubuntu server via GitHub Actions.

**Status**: Production, actively used. A SvelteKit rewrite (`family-kitchen-svelte`) is planned but not yet prioritized.

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

> No `.sln` file — build/test the `.csproj` directly (the commands above do).
> Docker build (MSB3552 workaround) + the prod deploy pipeline auto-load via `.claude/rules/deployment.md`. Adding an EF migration auto-loads via `.claude/rules/architecture.md`.

## Architecture (keystones)

**Stack**: .NET 10 / Blazor Server / PostgreSQL / MudBlazor / EF Core / Docker.

Load-bearing invariants — the full project layout + architectural patterns auto-load via `.claude/rules/architecture.md` when you open app source (`src/FamilyCoordinationApp/**`):
- **Multi-tenant isolation** — every entity has a composite key (`HouseholdId` + entity id) and **every query filters by `HouseholdId`**. It's a security boundary, not a convention.
- **DbContextFactory** — Blazor Server circuits are long-lived, so inject `IDbContextFactory<ApplicationDbContext>` (never `DbContext`) and create short-lived contexts via `dbFactory.CreateDbContextAsync()`.

Deployment + environment configuration detail auto-loads via `.claude/rules/deployment.md` when you open CI / Docker / deploy files.

## Roadmap

`.planning/ROADMAP.md` is the authoritative phase list + status. Forward (not-yet-built) work is also tracked in the Spine campaign **"Family Coordination App"** (`spine_map` to see the frontier). As of 2026-06-18 the open phases are **13 — multi-room chores (M:N)** and **15 — equity rework / invisible labor**, both spec-first and not started; core app Phases 1–7 and chores Phases 10–12 + 14 are shipped (8–9 deprecated).

## Corrections
<!-- Also see global corrections: D:\Development\data\memory\CORRECTIONS.md -->

- [2026-04-16 UTC] PREPEND `sudo` to docker commands on the production host over SSH. Reason: docker socket not in user group.

<!-- /reflect completed 2026-04-16 UTC -->
