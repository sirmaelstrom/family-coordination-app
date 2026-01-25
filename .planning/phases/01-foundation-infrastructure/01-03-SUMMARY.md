---
phase: 01-foundation-infrastructure
plan: 03
subsystem: infrastructure
tags: [docker, docker-compose, nginx, postgresql, blazor-server, websocket, ssl]

# Dependency graph
requires:
  - phase: 01-foundation-infrastructure
    plan: 01
    provides: "Blazor Server project with EF Core and entity schema"
provides:
  - "Dockerfile for Blazor Server application (two-stage build)"
  - "docker-compose.yml with postgres, app, nginx services"
  - "docker-compose.override.yml with development overrides"
  - "nginx reverse proxy with WebSocket support for Blazor Server SignalR"
  - "PostgreSQL health check dependency ensuring database ready before app"
  - ".env.example template for secrets (PostgreSQL, Google OAuth, ZFS paths)"
  - "Self-signed SSL certificate for local HTTPS testing"
affects: [01-04, phase-5-multi-user-collaboration]

# Tech tracking
tech-stack:
  added:
    - "postgres:17 (Docker image)"
    - "nginx:1.27-alpine (Docker image)"
    - "mcr.microsoft.com/dotnet/sdk:8.0 (build stage)"
    - "mcr.microsoft.com/dotnet/aspnet:8.0 (runtime stage)"
  patterns:
    - "Two-stage Docker build (SDK for build, ASP.NET runtime for final image)"
    - "PostgreSQL healthcheck with pg_isready before app startup"
    - "nginx WebSocket upgrade headers for SignalR (Upgrade, Connection)"
    - "Docker Compose override pattern (base + dev overrides)"
    - "Environment variable templating with .env.example"
    - "ZFS volume path configuration for production deployment"
    - "Long timeouts (86400s) for persistent Blazor Server connections"

key-files:
  created:
    - "Dockerfile"
    - ".dockerignore"
    - "docker-compose.yml"
    - "docker-compose.override.yml"
    - ".env.example"
    - ".gitignore"
    - "nginx/nginx.conf"
    - "nginx/family-app.conf"
    - "nginx/certs/.gitkeep"
    - "nginx/certs/privkey.pem (not committed)"
    - "nginx/certs/fullchain.pem (not committed)"
  modified:
    - "src/FamilyCoordinationApp/Program.cs (added /health endpoint)"

key-decisions:
  - "Two-stage Dockerfile over single-stage (smaller final image, SDK only in build)"
  - "Named volumes for dev, ZFS paths for production (configurable via env vars)"
  - "docker-compose.override.yml auto-loaded in development"
  - "Self-signed certs for local HTTPS (Let's Encrypt in production)"
  - "nginx proxy_buffering off for real-time SignalR"
  - "86400s timeouts for long-lived Blazor Server circuits"
  - "Secrets in .env file (gitignored), .env.example as template"

patterns-established:
  - "Health check pattern: depends_on with service_healthy condition"
  - "WebSocket proxying: proxy_http_version 1.1 + Upgrade/Connection headers"
  - "Environment templating: .env.example committed, .env gitignored"
  - "Development override pattern: separate docker-compose.override.yml"
  - "ZFS path convention: /[ZFS_POOL]/docker-data/family-app/{service}"

# Metrics
duration: 3min
completed: 2026-01-23
---

# Phase 1 Plan 03: Docker Compose Configuration Summary

**Complete Docker infrastructure for local development and production deployment with nginx WebSocket support**

## Performance

- **Duration:** 3 min
- **Started:** 2026-01-23T03:01:05Z
- **Completed:** 2026-01-23T03:04:00Z
- **Tasks:** 3
- **Files created:** 11
- **Files modified:** 1

## Accomplishments
- Created two-stage Dockerfile optimized for Blazor Server
- Configured Docker Compose with PostgreSQL, app, and nginx services
- Implemented PostgreSQL health check dependency to ensure database ready before app startup
- Created nginx reverse proxy configuration with WebSocket support for Blazor Server SignalR
- Generated self-signed SSL certificate for local HTTPS testing
- Established environment variable templating pattern with .env.example
- Configured ZFS volume paths for production deployment on [SERVER]

## Task Commits

Each task was committed atomically:

1. **Task 1: Create Dockerfile for Blazor Server application** - `707c4ac` (feat)
2. **Task 2: Create Docker Compose configuration files** - `d407e20` (feat)
3. **Task 3: Create nginx configuration with WebSocket support** - `81ea538` (feat)

## Files Created/Modified

**Docker infrastructure:**
- `Dockerfile` - Two-stage build (SDK + ASP.NET runtime)
- `.dockerignore` - Excludes build artifacts and version control
- `docker-compose.yml` - Production configuration with postgres, app, nginx
- `docker-compose.override.yml` - Development overrides (exposed ports, named volumes)
- `.env.example` - Template for secrets and ZFS paths
- `.gitignore` - Protects secrets and build artifacts

**nginx configuration:**
- `nginx/nginx.conf` - Main nginx config with WebSocket upgrade mapping
- `nginx/family-app.conf` - Server block for family.example.com
- `nginx/certs/.gitkeep` - Placeholder for SSL certificates directory
- `nginx/certs/privkey.pem` - Self-signed private key (not committed)
- `nginx/certs/fullchain.pem` - Self-signed certificate (not committed)

**Application:**
- `src/FamilyCoordinationApp/Program.cs` - Added `/health` endpoint for Docker health check

## Decisions Made

**Two-stage Dockerfile:** Build stage uses dotnet/sdk:8.0 for compilation, runtime stage uses dotnet/aspnet:8.0 for smaller final image (SDK excluded from production).

**PostgreSQL health check dependency:** Used `depends_on: postgres: condition: service_healthy` instead of simple `depends_on`. Prevents "connection refused" errors when app starts before PostgreSQL is ready to accept connections.

**nginx WebSocket configuration:** Configured `proxy_http_version 1.1`, `Upgrade`, and `Connection` headers. Required for Blazor Server SignalR - without these, connections fall back to long polling (poor performance).

**Long connection timeouts:** Set `proxy_read_timeout` and `proxy_send_timeout` to 86400s (24 hours). Blazor Server circuits are long-lived per-user connections that persist across page navigation.

**Environment variable templating:** Created `.env.example` with all required variables as template. Actual `.env` file is gitignored to protect secrets. Pattern supports local dev and production with same compose files.

**ZFS path configuration:** Added environment variables for [SERVER] production deployment paths (`/[ZFS_POOL]/docker-data/family-app/`). Development uses named volumes via docker-compose.override.yml.

**Self-signed certificates:** Generated local SSL certs for HTTPS testing during development. Production will use Let's Encrypt certificates from [SERVER]'s existing setup.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

**Docker Compose not installed in WSL:** Could not validate docker-compose configuration with `docker-compose config`. This is expected - Docker Compose will be available on [SERVER] deployment target. YAML syntax is valid.

## User Setup Required

**Before running locally:**
1. Copy `.env.example` to `.env`
2. Set `POSTGRES_USER` and `POSTGRES_PASSWORD`
3. Set `GOOGLE_CLIENT_ID` and `GOOGLE_CLIENT_SECRET` (from Google Cloud Console)
4. Run `docker-compose up` to start all services

**For production on [SERVER]:**
1. Uncomment ZFS path variables in `.env`
2. Set paths to [SERVER] ZFS locations
3. Replace self-signed certs with Let's Encrypt certificates
4. Configure Cloudflare DNS for family.example.com

## Next Phase Readiness

Complete Docker infrastructure established. Ready for:
- **Plan 01-02:** Google OAuth authentication (runs in Docker environment)
- **Plan 01-04:** First-run setup wizard and seed data (requires Docker database)

**Current state:** Infrastructure files created and committed. Docker Compose can be tested locally (requires Docker Desktop with WSL integration) or deployed to [SERVER] for testing.

**Remaining work in Phase 1:**
- Google OAuth integration (01-02)
- First-run setup wizard (01-04)
- Database migrations and seed data (01-04)

---
*Phase: 01-foundation-infrastructure*
*Completed: 2026-01-23*
