---
phase: 01-foundation-infrastructure
plan: 04
subsystem: initialization
tags: [setup-wizard, whitelist-admin, seed-data, first-run, blazor-server]

# Dependency graph
requires:
  - phase: 01-foundation-infrastructure
    plan: 01
    provides: "Entity schema with Household and User entities"
  - phase: 01-foundation-infrastructure
    plan: 02
    provides: "Google OAuth authentication"
provides:
  - "SetupService for first-run household creation"
  - "First-run setup redirect middleware"
  - "Setup wizard at /setup for household initialization"
  - "Whitelist admin page at /settings/users for user management"
  - "Personalized home dashboard with household name"
  - "Development seed data with 15 realistic recipes"
affects: [phase-2-recipe-management, phase-3-meal-planning, phase-4-shopping-list]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Middleware-based setup redirect pattern"
    - "Setup status caching for performance"
    - "IAsyncDisposable pattern for DbContext in Blazor components"
    - "Bogus library for realistic test data generation"
    - "Seed data conditional seeding (only if empty)"

key-files:
  created:
    - "src/FamilyCoordinationApp/Services/SetupService.cs"
    - "src/FamilyCoordinationApp/Components/Pages/Setup/FirstRunSetup.razor"
    - "src/FamilyCoordinationApp/Components/Pages/Settings/WhitelistAdmin.razor"
    - "src/FamilyCoordinationApp/Components/Pages/Home.razor"
    - "src/FamilyCoordinationApp/Data/SeedData.cs"
  modified:
    - "src/FamilyCoordinationApp/Program.cs"

key-decisions:
  - "Middleware approach for setup redirect (checks all requests automatically)"
  - "Cached setup status in SetupService (avoids repeated database queries)"
  - "First user auto-whitelisted during household creation"
  - "Any whitelisted user can manage whitelist (family trust model)"
  - "Prevent disabling last admin user (ensures access never lost)"
  - "Seed data runs automatically in development on startup"
  - "15 realistic recipe names (not generic 'Recipe 1')"

patterns-established:
  - "Setup flow: Sign in with Google → Enter household name → Redirect to home"
  - "Whitelist admin pattern: Any family member can add others"
  - "Pre-authenticated setup page with [AllowAnonymous] attribute"
  - "Setup status check skips static files, health checks, and setup routes"
  - "Seed data only runs once (checks if recipes exist)"

# Metrics
duration: 4min
completed: 2026-01-23
---

# Phase 1 Plan 04: First-Run Setup and Admin UI Summary

**First-run setup wizard, whitelist admin interface, and development seed data for realistic testing**

## Performance

- **Duration:** 4 min
- **Started:** 2026-01-23T03:08:37Z
- **Completed:** 2026-01-23T03:12:25Z
- **Tasks:** 3
- **Files created:** 5
- **Files modified:** 1

## Accomplishments
- Created SetupService with household creation and setup status checking
- Implemented first-run setup redirect middleware for automatic setup enforcement
- Built setup wizard guiding users through Google sign-in and household creation
- Created whitelist admin page for family member management
- Updated home page with personalized dashboard and quick links
- Implemented development seed data with 15 realistic recipes using Bogus library

## Task Commits

Each task was committed atomically:

1. **Task 1: Create SetupService and first-run middleware** - `13852a7` (feat)
2. **Task 2: Create first-run setup wizard page** - `1b9dd46` (feat)
3. **Task 3: Create whitelist admin page and update Home page** - `7459e2a` (feat)

## Files Created/Modified

**Services:**
- `src/FamilyCoordinationApp/Services/SetupService.cs` - Setup status checking and household creation

**Setup wizard:**
- `src/FamilyCoordinationApp/Components/Pages/Setup/FirstRunSetup.razor` - Two-step setup flow

**Admin and home pages:**
- `src/FamilyCoordinationApp/Components/Pages/Settings/WhitelistAdmin.razor` - User management interface
- `src/FamilyCoordinationApp/Components/Pages/Home.razor` - Personalized dashboard with quick links

**Seed data:**
- `src/FamilyCoordinationApp/Data/SeedData.cs` - Realistic recipe generation with Bogus

**Application configuration:**
- `src/FamilyCoordinationApp/Program.cs` - Middleware registration and seed data initialization

## Decisions Made

**Middleware for setup redirect:** Used middleware approach instead of checking in individual pages. Ensures all requests check setup status before proceeding, preventing access to any page before household creation.

**Cached setup status:** SetupService caches setup status in private field after first check. Avoids repeated database queries per request while still allowing reset when household is created.

**First user auto-whitelisted:** CreateHouseholdAsync sets IsWhitelisted=true for initial user. Ensures first user can immediately access app and add other family members.

**Any user can manage whitelist:** WhitelistAdmin page doesn't restrict to specific admin role. Family trust model - any whitelisted user can add or remove others. Simplifies access management for families.

**Last admin protection:** UI prevents disabling last active user. Prevents scenario where all users are disabled and app becomes inaccessible.

**Seed data in development only:** SeedDevelopmentDataAsync runs on startup only when IsDevelopment. Production environments won't be polluted with test data.

**Realistic recipe names:** Used actual recipe names (Spaghetti Bolognese, Chicken Stir Fry) instead of generic "Recipe 1, Recipe 2". Provides realistic testing experience and better demo presentation.

**Conditional seeding:** Seed data checks if recipes already exist before adding data. Prevents duplicate seed data on application restarts.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None.

## User Setup Required

**First-time setup flow:**
1. Navigate to app URL
2. Redirected to /setup
3. Click "Sign in with Google" (redirects to Google OAuth)
4. After Google authentication, enter household name
5. Click "Create Household"
6. Redirected to home dashboard

**Adding family members:**
1. Navigate to "Family Members" from home dashboard
2. Enter email address in "Add Family Member" form
3. Click "Add"
4. User is whitelisted and can sign in

**Development seed data:**
- Automatically runs on first startup in development environment
- Creates 15 recipes with random ingredients
- Only seeds if database has no existing recipes

## Next Phase Readiness

Phase 1 (Foundation & Infrastructure) is now complete. All infrastructure, authentication, and initialization components are in place.

Ready for:
- **Phase 2:** Recipe management with manual entry and URL import
- **Phase 3:** Meal planning with weekly calendar view
- **Phase 4:** Shopping list with ingredient aggregation

**Current state:**
- Database schema defined with composite keys for multi-tenant isolation
- Google OAuth authentication with email whitelist authorization
- Docker infrastructure with nginx reverse proxy and PostgreSQL
- First-run setup wizard creates household and initial user
- Whitelist admin allows family members to manage access
- Development seed data provides realistic testing environment

**Testing the setup:**
1. Ensure Google OAuth credentials configured in appsettings.json or user-secrets
2. Start Docker Compose: `docker-compose up`
3. Navigate to https://localhost (or configured port)
4. Follow first-run setup wizard
5. Verify seed data appears (development only)
6. Test whitelist admin by adding another email

---
*Phase: 01-foundation-infrastructure*
*Completed: 2026-01-23*
