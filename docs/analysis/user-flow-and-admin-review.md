# User Flow & Admin Features Review

*Created: 2026-01-25*
*Status: ✅ Analysis Complete*

## Questions to Answer

### New User Experience
- [x] Q1: What happens when a new user signs in who hasn't been set up for a household?
  - **Answer:** Redirected to `/account/access-denied` with message to contact admin
- [x] Q2: Can they create a new household?
  - **Answer:** No - only possible during first-run setup when no households exist
- [x] Q3: What's the first-run experience flow?
  - **Answer:** `/setup` page → Google sign-in → enter household name → create

### Access Control
- [x] Q4: Who can see the feedback admin page? (Just Justin, any authenticated user, or role-based?)
  - **Answer:** Any authenticated/whitelisted user sees ALL feedback (including other households)
- [x] Q5: How does the whitelist system work for inviting family?
  - **Answer:** Admin adds email → User record created with HouseholdId → they can sign in
- [x] Q6: What happens when an invited family member signs in - auto-association with household?
  - **Answer:** Already associated at whitelist time; login just updates profile fields

### Edge Cases
- [x] Q7: What if multiple households whitelist the same email address?
  - **Answer:** Not possible - email is globally unique (DB constraint)
- [x] Q8: Justin has a second Gmail as API owner, set up under family but can only disable - what's the expected behavior?
  - **Answer:** "Last admin" protection - can't disable the last whitelisted user
- [x] Q9: Is there a way to fully delete accounts (not just disable)?
  - **Answer:** No - only disable (toggle `IsWhitelisted`)

### Readiness Assessment
- [x] Q10: Is the app ready to share with family, or are there gaps?
  - **Answer:** Yes for single family. Gaps: no deletion, no admin roles, feedback visible to all

---

## Proposed Features

### High Priority - Admin Visibility
1. **Server Admin Role**
   - Special role for Justin (site owner)
   - Separate from household admin
   - Access to cross-household data for troubleshooting

2. **Admin Dashboard** (new page: /admin or /settings/admin)
   - View all households
   - View all users (with household association)
   - User management (delete, reassign household)
   - System health/stats

3. **Account Deletion**
   - Users can request deletion
   - Admin can delete users
   - Handle orphaned data (recipes, etc.)

### Medium Priority - UX Improvements
4. **Collapsible Navigation Menu**
   - MudBlazor MudDrawer with toggle
   - Remember collapsed state (localStorage or user preference)

5. **Navigation Placement Options** (larger feature)
   - Left sidebar (current)
   - Top navbar
   - Bottom mobile-style nav
   - User preference stored in DB

### Lower Priority - Nice to Have
6. **Other Admin Features** (brainstorm)
   - Activity logs
   - Email/invite history
   - Recipe import stats
   - Error logs viewer
   - Feature flags

---

## Research Tasks (Sonnet)

1. Audit current user registration/household flow in codebase
2. Check SetupService and whitelist logic
3. Review FeedbackAdmin access control
4. Identify gaps in account management
5. Assess multi-household email conflict handling

---

## Findings

### 1. New User Flow

#### Brand New User (Not Whitelisted)
1. User goes to any page → middleware redirects to `/setup` if no households exist
2. If households exist but user isn't whitelisted:
   - User signs in with Google
   - `WhitelistedEmailHandler` checks `Users` table for matching email with `IsWhitelisted=true`
   - **No match found → authorization fails**
   - User redirected to `/account/access-denied` with message to contact family admin

**Key code** (`WhitelistedEmailHandler.cs`):
```csharp
var user = await dbContext.Users
    .FirstOrDefaultAsync(u => u.Email == email && u.IsWhitelisted);

if (user is not null)
{
    context.Succeed(requirement);  // Allowed in
}
// Otherwise: silently fails → access denied
```

#### Whitelisted User (First Sign-In)
1. Family admin adds email via `/settings/users` (WhitelistAdmin)
2. Creates `User` record with:
   - `Email` set
   - `DisplayName` = email prefix (before @)
   - `GoogleId = null` (populated on first login)
   - `IsWhitelisted = true`
   - `HouseholdId` = admin's household
3. When whitelisted user signs in:
   - `WhitelistedEmailHandler` finds matching record
   - Updates `LastLoginAt`, `PictureUrl`, `Initials` from Google profile
   - User is authorized and sees household content

**Note:** GoogleId is NOT updated on first login by the handler - only profile fields. This is a minor gap (handler doesn't set GoogleId).

#### Can Users Create New Households?
**No** - after initial setup:
- `/setup` page shows "Setup is already complete!" if any household exists
- No UI exists for creating additional households
- `SetupService.CreateHouseholdAsync` is only called from FirstRunSetup

**Current model:** Single household per instance. Multi-household would require:
- A way for authenticated users to create new households
- Changes to whitelist (currently just `[Authorize]` scopes to user's household)

---

### 2. Access Control - FeedbackAdmin

**Current state:** `FeedbackAdmin.razor` uses `@attribute [Authorize]` only.

```csharp
// Query shows ALL feedback across ALL households
_feedbackItems = await context.Feedbacks
    .Include(f => f.User)
    .Include(f => f.Household)
    .OrderByDescending(f => f.CreatedAt)
    .ToListAsync();
```

**Issues:**
- ✅ Requires authentication + whitelist (via DefaultPolicy)
- ❌ **No household filtering** - any authenticated user sees ALL feedback from ALL households
- ❌ **No admin role check** - every family member can see/manage feedback
- ❌ Shows other households' feedback (privacy concern if multi-household)

**For single-household deployment:** This is fine - all family members can see feedback.
**For multi-household:** Privacy violation - users can see other families' feedback.

---

### 3. Whitelist System

#### How Inviting Works (`WhitelistAdmin.razor`)
Admin enters email → creates User record:

```csharp
var newUser = new User
{
    HouseholdId = _householdId,  // Current user's household
    Email = email,
    DisplayName = email.Split('@')[0],
    GoogleId = null,  // Set when user first logs in with Google
    IsWhitelisted = true,
    CreatedAt = DateTime.UtcNow
};
```

**Key points:**
- Whitelisted users are pre-created with `HouseholdId` set
- No invitation email is sent (manual communication required)
- User is immediately associated with household on creation, not on first login

#### Household Association
- Association happens at whitelist time, not login time
- `WhitelistedEmailHandler` just validates - doesn't modify household membership
- All household-scoped pages query by `HouseholdId` from current user

---

### 4. Edge Cases

#### Can Same Email Be in Multiple Households?
**No** - prevented by database constraint:

```csharp
// UserConfiguration.cs
builder.HasIndex(u => u.Email)
    .IsUnique();  // Global unique, not per-household
```

This means:
- Email can only exist in ONE household
- If user is already in a household, adding them to another fails at DB level
- No UI handling for this conflict

#### Disable vs Delete User
**Current behavior:**
- **Disable:** Sets `IsWhitelisted = false` - user can't log in
- **Delete:** Not implemented

```csharp
// WhitelistAdmin.razor - only toggle, no delete
private async Task ToggleUser(User user)
{
    user.IsWhitelisted = !user.IsWhitelisted;
    await _context.SaveChangesAsync();
}
```

**Protection:** Can't disable the last admin (guards against orphaned household):
```razor
@if (context.IsWhitelisted && _users.Count(u => u.IsWhitelisted) > 1)
{
    <MudButton OnClick="() => ToggleUser(context)">Disable</MudButton>
}
else
{
    <MudText>Last admin</MudText>  // Can't disable
}
```

#### Account Deletion
**Not implemented.** No way to:
- User self-delete
- Admin delete other users
- Delete orphaned data (recipes created by deleted user, etc.)

---

### 5. Current Admin Capabilities

#### What Exists
| Feature | Location | Scope |
|---------|----------|-------|
| Manage family members | `/settings/users` | Household |
| View/manage feedback | `/settings/feedback` | **Global** (bug!) |
| Manage categories | `/settings/categories` | Household |

#### Role System
**None exists.** User entity has no `Role`, `IsAdmin`, or `IsOwner` field.

```csharp
// User.cs - no role fields
public class User
{
    public int Id { get; set; }
    public int HouseholdId { get; set; }
    public string Email { get; set; }
    public string DisplayName { get; set; }
    public string? GoogleId { get; set; }
    public bool IsWhitelisted { get; set; }  // Only access control
    // No IsAdmin, Role, etc.
}
```

**Implications:**
- All whitelisted users are equal
- No "household owner" concept
- No "site admin" concept
- Anyone can add/disable family members
- Anyone can see all feedback

---

### Summary Table

| Question | Answer |
|----------|--------|
| New user (not whitelisted) | Access denied page |
| Whitelisted user first login | Works, profile updated |
| Create new households | Only on first-run, not after |
| Who sees feedback admin | All authenticated users (bug) |
| Same email in multiple households | Not allowed (unique constraint) |
| Disable vs delete | Disable only, no delete |
| Admin roles | None exist |
| Site admin concept | None exists |

---

## Recommendations

### Critical (Security/Privacy)
1. **Fix FeedbackAdmin household scoping** - Filter feedback by current user's household
   ```csharp
   var currentUserHouseholdId = /* get from auth state */;
   _feedbackItems = await context.Feedbacks
       .Where(f => f.HouseholdId == currentUserHouseholdId)
       .Include(f => f.User)
       .ToListAsync();
   ```

### High Priority (Admin Features)
2. **Add site admin role** - New field on User: `bool IsSiteAdmin`
   - Or: config-based admin email list (simpler for single-admin)
   - Site admin can see all households, all feedback, manage all users

3. **Add account deletion**
   - Admin can delete users (with orphan handling)
   - Consider: reassign recipes to "Deleted User" or household

4. **Set GoogleId on first login** - Handler should update `GoogleId` if null

### Medium Priority (UX)
5. **Household creation for existing users** - If multi-household is desired
6. **Invitation system** - Email invites instead of manual "add and tell them"
7. **Activity logging** - Track who added/disabled whom

### Low Priority (Nice to Have)
8. **Household admin role** - Separate from site admin
9. **User self-service** - View own data, request deletion

### Ready for Family?
**Yes, with caveats:**
- ✅ Core functionality works (recipes, meal plans, shopping lists)
- ✅ Whitelist system prevents unauthorized access
- ⚠️ Any family member can see all feedback (minor for single family)
- ⚠️ No user deletion (can disable though)
- ⚠️ No admin hierarchy (everyone equal)

For a single-family deployment, these issues are minor. The feedback visibility "bug" only matters if you consider some feedback private within the family.
