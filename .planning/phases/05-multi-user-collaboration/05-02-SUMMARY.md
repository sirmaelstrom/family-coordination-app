---
phase: 05
plan: 02
completed: 2026-01-24
duration: 1 min
subsystem: auth
tags: [oauth, profile, google, avatars]

requires:
  - phase: 05
    plan: 01
    artifact: User entity with PictureUrl and Initials columns

provides:
  - Google OAuth picture claim mapping
  - User profile auto-update on authentication
  - Initials computation from display name

affects:
  - phase: 05
    plan: 03
    reason: Avatar component will consume PictureUrl/Initials

tech-stack:
  added: []
  patterns:
    - OAuth claim mapping for profile enrichment
    - Automatic profile updates on authentication

key-files:
  created: []
  modified:
    - src/FamilyCoordinationApp/Program.cs
    - src/FamilyCoordinationApp/Authorization/WhitelistedEmailHandler.cs

decisions:
  - decision: Update picture URL and initials on every login
    rationale: Ensures app reflects Google profile changes without requiring manual updates
    alternatives: Only update on first login (would miss profile changes)
    impact: Minimal (single extra DB write per login)
  - decision: Compute initials from first and last name characters
    rationale: Standard convention, handles most Western naming patterns
    alternatives: Use first two characters of display name (less readable), allow customization (over-engineered)
    impact: Single-letter initials for single-name users, fallback to "?" for empty names

commits:
  - hash: 8306cb9
    message: "feat(05-02): map Google OAuth picture claim"
    files: [src/FamilyCoordinationApp/Program.cs]
  - hash: 0a3304a
    message: "feat(05-02): store picture URL and initials on user login"
    files: [src/FamilyCoordinationApp/Authorization/WhitelistedEmailHandler.cs]
---

# Phase 05 Plan 02: OAuth Profile Picture Summary

**One-liner:** Google OAuth picture claim captured and user initials computed from display name on authentication.

## What Was Built

Google OAuth integration enhanced to capture user profile pictures and compute initials during authentication flow.

**Key capabilities:**
- Google OAuth picture claim mapped to `urn:google:picture` for extraction
- WhitelistedEmailHandler extracts picture URL from OAuth response
- User initials automatically computed from DisplayName (first + last character)
- User.PictureUrl and User.Initials updated on every login (not just first login)

**Profile update strategy:**
- Every successful authentication updates user profile
- Captures Google profile picture URL changes automatically
- Initials recomputed in case DisplayName changes
- Minimal overhead (single UPDATE per login)

## Implementation Details

### Google OAuth Configuration (Program.cs)

Added picture claim mapping to Google OAuth setup:

```csharp
// Map picture claim for avatar display
options.ClaimActions.MapJsonKey("urn:google:picture", "picture", "url");
```

The "profile" scope was already requested, which includes picture URL in OAuth response.

### Authentication Handler (WhitelistedEmailHandler.cs)

Enhanced authorization handler to extract and store profile data:

**Claim extraction:**
```csharp
var pictureClaim = context.User.FindFirst("urn:google:picture")?.Value;
var displayName = context.User.FindFirst(ClaimTypes.Name)?.Value ?? user.DisplayName;
```

**Initials computation logic:**
```csharp
private static string ComputeInitials(string displayName)
{
    if (string.IsNullOrWhiteSpace(displayName))
        return "?";

    var parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0)
        return "?";
    if (parts.Length == 1)
        return parts[0][0].ToString().ToUpperInvariant();

    // First and last name initials
    return $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
}
```

**Profile update on login:**
```csharp
user.LastLoginAt = DateTime.UtcNow;
user.PictureUrl = pictureClaim;
user.Initials = ComputeInitials(displayName);
await dbContext.SaveChangesAsync();
```

### Initials Algorithm

Edge cases handled:
- Empty/whitespace name → "?"
- Single word (e.g., "Madonna") → First character (e.g., "M")
- Multiple words (e.g., "John Smith") → First + last characters (e.g., "JS")
- Always uppercased for consistency

## Verification Results

All verification checks passed:
- ✓ `dotnet build` succeeded (0 warnings, 0 errors)
- ✓ Google OAuth picture claim mapping exists in Program.cs
- ✓ WhitelistedEmailHandler extracts and stores PictureUrl
- ✓ ComputeInitials function implemented with edge case handling

## Decisions Made

**1. Update profile on every login (not just first login)**

Every authentication updates PictureUrl and Initials, even for existing users.

- **Rationale:** Users may change their Google profile picture or name. Without this, app would display stale data until manual update.
- **Alternatives considered:** Only update on user creation (misses profile changes)
- **Impact:** Minimal performance impact (single UPDATE per login). Ensures app stays in sync with Google profile.

**2. Compute initials from first and last name**

Uses first character of first word + first character of last word.

- **Rationale:** Standard convention for initials (e.g., "John Smith" → "JS"). Handles most Western naming patterns.
- **Alternatives considered:**
  - First two characters of name (e.g., "John" → "JO") - less readable
  - Allow users to customize initials - over-engineered for current needs
- **Impact:** Single-letter initials for single-name users (e.g., "Madonna" → "M"). Fallback to "?" for empty names.

## Next Phase Readiness

**Ready to proceed to plan 05-03 (Avatar component)**

This plan provides the data foundation:
- User.PictureUrl populated from Google OAuth
- User.Initials computed as fallback
- Data updated on every authentication

Avatar component can now:
1. Display `<img src="@user.PictureUrl">` if available
2. Fall back to `<div>@user.Initials</div>` if picture unavailable/fails to load
3. Trust that data stays current (updated on every login)

**No blockers identified.**

## Testing Notes

**Manual verification needed:**
1. Login with Google account that has profile picture
2. Check database: `SELECT Email, PictureUrl, Initials FROM Users`
3. Verify PictureUrl contains Google CDN URL
4. Verify Initials match first+last name characters
5. Change Google profile picture, re-login, verify PictureUrl updated

**Expected behavior:**
- PictureUrl: `https://lh3.googleusercontent.com/...` (Google CDN)
- Initials: Two uppercase letters for multi-word names, one for single-word

## Deviations from Plan

None - plan executed exactly as written.

## Dependencies

**Requires:**
- Phase 05 Plan 01: User entity with PictureUrl and Initials columns

**Provides for:**
- Phase 05 Plan 03: Avatar component data source

**Dependencies met:** Database schema from 05-01 in place, authentication flow operational.

## Metrics

- **Files modified:** 2
- **Lines added:** 25
- **Lines removed:** 1
- **Build time:** 14.74s
- **Commits:** 2
- **Duration:** 1 minute
