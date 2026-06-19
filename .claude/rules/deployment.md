---
paths:
  - ".github/workflows/*.yml"
  - "Dockerfile*"
  - "docker-compose*.yml"
  - "docker-build.sh"
  - "docker-dev.sh"
  - "deploy.sh"
  - "deploy-from-windows.ps1"
  - "DEPLOY-SAFETY.md"
  - "DOCKER-BUILD-WORKAROUND.md"
---

# Deployment & environment configuration

*Path-scoped rule — auto-loads when you open CI workflows, Docker/compose files, or deploy scripts. Day-to-day build/test commands stay always-on in `CLAUDE.md`; this carries the prod ship pipeline + env contract.*

## Docker Build

There is a known .NET SDK 10.0 bug (MSB3552) that breaks multi-stage Docker builds. See `DOCKER-BUILD-WORKAROUND.md`. Use the workaround script:

```bash
./docker-build.sh          # Build with 'latest' tag
./docker-build.sh v1.0.0   # Build with specific tag
```

## Deployment

Push to `master` triggers GitHub Actions:
1. **CI** (`ci.yml`): Build, test, format check, Docker build validation (GitHub-hosted runners)
2. **Deploy** (`deploy.yml`): Self-hosted runner pulls code, builds Docker image, deploys via `docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d`

Production traffic flow: `Internet → Cloudflare → <server-hostname> (host nginx) → app container`

(On the production host over SSH, prepend `sudo` to docker commands — see CLAUDE.md `## Corrections`.)

## Environment Configuration

Required env vars (set via `.env` on the server, generated from a secrets manager during deploy):
- `ConnectionStrings__DefaultConnection` — PostgreSQL connection string
- `Authentication__Google__ClientId` — Google OAuth client ID
- `Authentication__Google__ClientSecret` — Google OAuth client secret
- `SITE_ADMIN_EMAILS` — comma-separated admin emails
- `DATAPROTECTION_CERT` — optional base64 PFX for key encryption
