---
phase: 01-foundation-infrastructure
plan: 02
subsystem: authentication
tags: [google-oauth, authorization, whitelist, cookie-auth, blazor-server]

# Dependency graph
requires:
  - phase: 01-foundation-infrastructure
    plan: 01
    provides: "Blazor Server project with EF Core and User entity"
provides:
  - "Google OAuth authentication with persistent cookie (30-day expiry)"
  - "Email whitelist authorization via custom handler"
  - "WhitelistedEmailRequirement and WhitelistedEmailHandler"
  - "FallbackPolicy applying whitelist check to all authenticated requests"
  - "Login, Logout, and AccessDenied pages"
  - "Auth endpoints (MapPost /account/login-google, MapGet /account/logout)"
  - "Forwarded headers middleware for nginx reverse proxy"
affects: [01-03, 01-04, phase-2-recipe-management, phase-3-meal-planning, phase-4-shopping-list, phase-5-multi-user-collaboration]

# Tech tracking
tech-stack:
  added:
    - "Microsoft.AspNetCore.Authentication.Google 10.0.2"
  patterns:
    - "Cookie-based authentication with Google OAuth challenge scheme"
    - "Custom authorization requirement and handler pattern"
    - "FallbackPolicy for global authorization enforcement"
    - "Forwarded headers middleware configuration for reverse proxy"
    - "IHttpContextAccessor for component HttpContext access"

key-files:
  created:
    - "src/FamilyCoordinationApp/Authorization/WhitelistedEmailRequirement.cs"
    - "src/FamilyCoordinationApp/Authorization/WhitelistedEmailHandler.cs"
    - "src/FamilyCoordinationApp/Components/Pages/Account/Login.razor"
    - "src/FamilyCoordinationApp/Components/Pages/Account/Logout.razor"
    - "src/FamilyCoordinationApp/Components/Pages/Account/AccessDenied.razor"
  modified:
    - "src/FamilyCoordinationApp/FamilyCoordinationApp.csproj"
    - "src/FamilyCoordinationApp/Program.cs"
    - "src/FamilyCoordinationApp/appsettings.json"
    - "src/FamilyCoordinationApp/Components/Layout/MainLayout.razor"
    - "src/FamilyCoordinationApp/Components/_Imports.razor"

key-decisions:
  - "30-day cookie expiration with sliding expiration (session persists across browser restarts)"
  - "POST endpoint for Google OAuth challenge (CSRF protection)"
  - "Forwarded headers middleware first in pipeline (required for nginx reverse proxy)"
  - "Handler updates LastLoginAt timestamp on successful authorization"
  - "Scoped handler lifetime (matches typical authorization handler pattern)"
  - "AllowAnonymous on Logout and AccessDenied pages"

patterns-established:
  - "Auth endpoints use minimal API (MapPost, MapGet) not controller actions"
  - "Login page redirects authenticated users to home"
  - "MainLayout uses AuthorizeView for conditional user display"
  - "Global authorization via FallbackPolicy (no need for [Authorize] attributes)"

# Metrics
duration: 5min
completed: 2026-01-23
---

# Phase 1 Plan 02: Google OAuth Authentication Summary

**Google OAuth with email whitelist authorization, persistent sessions, and friendly access control pages**

## Performance

- **Duration:** 5 min
- **Started:** 2026-01-23T03:01:00Z
- **Completed:** 2026-01-23T03:06:08Z
- **Tasks:** 3
- **Files modified:** 11

## Accomplishments
- Configured Google OAuth authentication with cookie-based scheme
- Implemented custom email whitelist authorization requirement and handler
- Created login, logout, and access denied pages with Tailwind CSS styling
- Added auth endpoints using minimal API pattern
- Configured forwarded headers middleware for nginx reverse proxy compatibility
- Applied FallbackPolicy to enforce whitelist check on all authenticated requests

## Task Commits

Each task was committed atomically:

1. **Task 1: Configure Google OAuth authentication in Program.cs** - `a68194d` (feat)
2. **Task 2: Create custom authorization requirement and handler for email whitelist (AUTH-02)** - `e441b5d` (feat)
3. **Task 3: Create login, logout, and access denied pages with auth endpoints** - `4b3080a` (feat)

## Files Created/Modified

**Authentication configuration:**
- `src/FamilyCoordinationApp/FamilyCoordinationApp.csproj` - Added Microsoft.AspNetCore.Authentication.Google 10.0.2
- `src/FamilyCoordinationApp/Program.cs` - Authentication, authorization, and forwarded headers middleware
- `src/FamilyCoordinationApp/appsettings.json` - Added Authentication:Google configuration structure

**Authorization handlers:**
- `src/FamilyCoordinationApp/Authorization/WhitelistedEmailRequirement.cs` - IAuthorizationRequirement marker
- `src/FamilyCoordinationApp/Authorization/WhitelistedEmailHandler.cs` - Handler validating email against User table

**Account pages:**
- `src/FamilyCoordinationApp/Components/Pages/Account/Login.razor` - Google OAuth button with branding
- `src/FamilyCoordinationApp/Components/Pages/Account/Logout.razor` - Sign-out confirmation page
- `src/FamilyCoordinationApp/Components/Pages/Account/AccessDenied.razor` - Whitelist explanation page

**Layout updates:**
- `src/FamilyCoordinationApp/Components/Layout/MainLayout.razor` - User email display and logout link
- `src/FamilyCoordinationApp/Components/_Imports.razor` - Authorization usings

## Decisions Made

**30-day persistent cookie:** Cookie expiry set to 30 days with sliding expiration. Per AUTH-03 requirement, sessions must persist across browser restarts. Active users remain logged in without repeated authentication.

**Forwarded headers first in middleware pipeline:** UseForwardedHeaders called before all other middleware. When running behind nginx reverse proxy, ASP.NET Core needs forwarded headers to correctly determine scheme (https) and host for OAuth redirect URIs. Without this, OAuth callback fails with redirect_uri_mismatch.

**POST endpoint for Google OAuth challenge:** Login form posts to /account/login-google (not GET). Provides CSRF protection via anti-forgery token handling.

**Handler updates LastLoginAt:** WhitelistedEmailHandler updates User.LastLoginAt timestamp on successful authorization. Tracks user activity without separate login tracking logic.

**Scoped handler lifetime:** WhitelistedEmailHandler registered as scoped (not singleton). Matches typical authorization handler pattern and allows per-request logging context.

**AllowAnonymous on Logout and AccessDenied:** Logout and AccessDenied pages marked with [AllowAnonymous] attribute. Users who fail whitelist check need to access AccessDenied page; logged-out users need to see Logout confirmation.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None.

## User Setup Required

**Google OAuth Client ID and Secret:**

Users must create Google OAuth credentials before authentication works:

1. Visit Google Cloud Console: https://console.cloud.google.com
2. Navigate to: APIs & Services → Credentials → Create Credentials → OAuth 2.0 Client ID
3. Application type: Web application
4. Add authorized redirect URIs:
   - Development: `https://localhost:7xxx/signin-google` (replace xxx with actual port)
   - Production: `https://your-domain.example.com/signin-google`
5. Copy Client ID and Client Secret
6. Set environment variables:
   - `Authentication__Google__ClientId`
   - `Authentication__Google__ClientSecret`

**Development setup:**
```bash
# Using dotnet user-secrets
dotnet user-secrets set "Authentication:Google:ClientId" "YOUR_CLIENT_ID"
dotnet user-secrets set "Authentication:Google:ClientSecret" "YOUR_CLIENT_SECRET"
```

**Docker setup (Plan 01-03):**
```bash
# In .env file
Authentication__Google__ClientId=YOUR_CLIENT_ID
Authentication__Google__ClientSecret=YOUR_CLIENT_SECRET
```

Without these credentials, application will throw `InvalidOperationException: "Google ClientId not configured"` on startup.

## Next Phase Readiness

Complete authentication and authorization infrastructure established. Ready for:
- **Plan 01-03:** Docker Compose configuration with nginx WebSocket support
- **Plan 01-04:** First-run setup wizard to create initial household and whitelist admin user

**Note:** Application compiles and runs, but authentication requires Google OAuth credentials (user setup). Database does not exist yet (will be created in Plan 01-03/01-04 with migrations).

---
*Phase: 01-foundation-infrastructure*
*Completed: 2026-01-23*
