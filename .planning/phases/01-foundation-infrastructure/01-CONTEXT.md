# Phase 1: Foundation & Infrastructure - Context

**Gathered:** 2026-01-22
**Status:** Ready for planning

<domain>
## Phase Boundary

Establish database schema with multi-tenant isolation patterns and Google OAuth authentication. Creates the data foundation (all entities) and access control that subsequent phases depend on. Includes Docker Compose configuration for local development and future deployment.

</domain>

<decisions>
## Implementation Decisions

### Database Schema Approach
- Create complete entity schema upfront (User, Household, Recipe, MealPlan, ShoppingList, etc.) even if some tables unused until later phases
- Enforce multi-tenant isolation at database level with composite foreign keys (HouseholdId + EntityId)
- PostgreSQL composite primary keys and composite foreign keys prevent cross-household data leaks
- First household created via first-run setup page (setup wizard on first access)

### Authentication Flow
- Failed login shows friendly message: "This app is for family members only. Contact [admin] for access."
- Whitelist management via admin UI in app (not just environment variable)
- All family members can manage whitelist (any whitelisted user can add others)

### Docker Deployment Setup
- Phase 1 delivers Docker Compose files only (test locally, deploy to production-server in later phase)
- Secrets managed via `.env` file (gitignored, not in version control)
- PostgreSQL database and uploads map to explicit ZFS paths (/themanjesus/docker-data/family-app/)
- Include nginx configuration (provide complete server block for family.example.com)
- Single-stage Dockerfile (simpler, includes SDK in final image)
- Logs go to console + file volume (/app/logs mapped to ZFS for persistence)

### Development Workflow
- Local development uses Docker Compose (match production environment with docker-compose up)
- Include seed data for development (sample recipes, meal plans for testing)

### Claude's Discretion
- Recipe image storage format (relative paths vs just filename) - best practice for portability
- Post-logout flow (landing page vs straight to Google login) - appropriate UX
- Database migration timing (startup vs manual vs separate container) - reliability vs simplicity
- Google OAuth client separation (dev vs prod) - security best practice
- Docker Compose override file usage (separate dev/prod configs) - configuration management
- EF Core migration workflow (manual vs auto-generate script) - developer experience

</decisions>

<specifics>
## Specific Ideas

- ZFS paths follow existing production-server convention: `/themanjesus/docker-data/family-app/`
- Admin UI should be simple - just a settings page to add/remove email addresses from whitelist
- Seed data should include realistic recipe examples (not just "Test Recipe 1")
- Nginx config needs WebSocket support for future SignalR (Phase 5)

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope

</deferred>

---

*Phase: 01-foundation-infrastructure*
*Context gathered: 2026-01-22*
