# Family Coordination App

A household coordination app — meal planning, recipe management, shopping lists,
chores, and multi-user family collaboration.

The UI is a **SvelteKit single-page app (Svelte 5)** served at the site root by
an **ASP.NET Core** backend, which exposes the data layer under `/api` with
same-origin cookie auth. The only server-rendered pages are a small set of
static Razor Pages (login, legal, error, and the household-onboarding flow).

> Originally a Blazor Server app; flipped to the SvelteKit SPA in the de-Blazor
> migration (2026-07-04). Blazor Server and MudBlazor have been removed.

## Architecture

- **Backend** — .NET 10 / ASP.NET Core: minimal-API endpoints under `/api` plus
  the static Razor Pages. PostgreSQL via EF Core. Multi-tenant: every entity is
  keyed by `HouseholdId` and every query filters on it.
- **Frontend** — SvelteKit (Svelte 5, `adapter-static`) in `frontend/app/`,
  built to a static SPA and served at the site root. The build output is copied
  into the backend's `wwwroot/` (the `CopyAppSpa` MSBuild target locally, a
  Docker stage in prod) and served via per-prefix SPA fallbacks in `Program.cs`.
- **Auth** — Google OAuth + a 30-day sliding cookie (`FamilyApp.Auth`), scoped
  to an email whitelist.

## Building and Running

Requires the .NET 10 SDK, Node 20+, PostgreSQL, and Google OAuth credentials.

### Local development

**Fast frontend loop (Vite + hot reload)** — run the backend and the Vite dev
server side by side:

```bash
# Terminal 1 — backend on http://localhost:5077 (Development dev-auth bypass)
dotnet run --project src/FamilyCoordinationApp/FamilyCoordinationApp.csproj

# Terminal 2 — SvelteKit dev server on http://localhost:5174, proxies /api -> :5077
cd frontend/app && npm install && npm run dev
```

Open <http://localhost:5174>.

**Full app from the .NET host (SPA served at root)** — build the SPA first, then
run the backend. `dotnet run` copies `frontend/app/build/` into `wwwroot/` via
the `CopyAppSpa` target, but it does **not** build the SPA for you, so the build
step is required whenever the frontend changes:

```bash
cd frontend/app && npm install && npm run build
cd ../..
dotnet run --project src/FamilyCoordinationApp/FamilyCoordinationApp.csproj
```

Open <http://localhost:5077> (the HTTPS profile is `https://localhost:7130` — see
`src/FamilyCoordinationApp/Properties/launchSettings.json`).

### Build & test

```bash
# Backend build
dotnet build src/FamilyCoordinationApp/FamilyCoordinationApp.csproj

# Tests (xUnit + FluentAssertions + Moq + Bogus)
dotnet test tests/FamilyCoordinationApp.Tests/FamilyCoordinationApp.Tests.csproj

# Frontend type-check + build
cd frontend/app && npm install && npm run check && npm run build
```

### Docker

The production image is a single multi-stage `Dockerfile` — a `node:20-alpine`
stage builds the SPA, then the .NET publish stage copies it into `wwwroot/`:

```bash
# Build (same command prod uses)
docker build -t familyapp:latest .

# Run the app + PostgreSQL
docker compose up -d          # app is served on http://localhost:8080
```

On local Windows a known .NET SDK 10.0 bug (MSB3552) can break the multi-stage
build. If you hit it, use the workaround script (local `dotnet publish` +
`Dockerfile.runtime-only`) — see
[DOCKER-BUILD-WORKAROUND.md](DOCKER-BUILD-WORKAROUND.md):

```bash
./docker-build.sh [tag]       # default tag: latest
```

## Project Structure

- `src/FamilyCoordinationApp/` — ASP.NET Core backend (`/api` minimal-API
  endpoints, EF Core data layer, static Razor Pages, auth)
- `frontend/app/` — the SvelteKit SPA (Svelte 5) — the app's entire UI
- `tests/FamilyCoordinationApp.Tests/` — unit & integration tests
- `Dockerfile` — multi-stage production build (SPA + .NET)
- `Dockerfile.runtime-only` / `docker-build.sh` — local Windows MSB3552 workaround
- `docker-compose*.yml` — local and production Compose definitions

## Technology Stack

- .NET 10 / ASP.NET Core (minimal APIs + static Razor Pages)
- SvelteKit + Svelte 5 + TypeScript (`adapter-static` SPA)
- PostgreSQL + Entity Framework Core 10
- Google OAuth (same-origin cookie auth)
- Docker

## Features

- **Recipe Management** — import from URLs (schema.org JSON-LD), manual entry,
  ingredient parsing
- **Meal Planning** — weekly calendar with drag-and-drop scheduling
- **Shopping Lists** — auto-generated from meal plans with ingredient
  consolidation
- **Chores** — assignment, rotation, and household equity tracking
- **Multi-Household** — family collaboration with role-based permissions and
  household-to-household recipe sharing
- **Google OAuth** — secure authentication

## License

MIT License
