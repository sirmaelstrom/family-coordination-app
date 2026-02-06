# Cross-Family Recipe Sharing

## Overview

Allow users to share recipes between households (e.g., sharing a recipe with extended family who have their own Family Kitchen account).

## Current State

- Recipes are scoped to a single `HouseholdId`
- Users belong to one household
- No mechanism to share or copy recipes between households
- `UserFavorite` exists for personal favorites within a household

## Design Options

### Option 1: Share Link (Recommended - Simplest MVP)

Generate a unique, time-limited share link that allows another household to **copy** the recipe.

**New Entities:**
```csharp
public class RecipeShareLink
{
    public Guid Id { get; set; }
    public int HouseholdId { get; set; }
    public int RecipeId { get; set; }
    public string ShareCode { get; set; } // Short URL-safe code
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; } // Optional expiration
    public int? MaxUses { get; set; } // Optional use limit
    public int UseCount { get; set; }
    public int CreatedByUserId { get; set; }

    // Navigation
    public Recipe Recipe { get; set; }
    public User CreatedBy { get; set; }
}
```

**Flow:**
1. User clicks "Share" on a recipe
2. System generates a unique share code (e.g., `abc123xy`)
3. User shares link: `family.heathdev.me/share/abc123xy`
4. Recipient (logged in) visits link
5. System shows recipe preview with "Add to My Recipes" button
6. Clicking adds a **copy** to recipient's household
7. Copy includes attribution: "Shared from [OriginalHouseholdName]"

**Pros:**
- Simple implementation
- No ongoing relationship to manage
- Works with existing household isolation model
- Privacy-preserving (no household discovery)

**Cons:**
- No automatic updates if original changes
- Creates duplicate data

### Option 2: Household Connections

Allow households to "connect" and browse each other's recipes.

**New Entities:**
```csharp
public class HouseholdConnection
{
    public int Id { get; set; }
    public int RequestingHouseholdId { get; set; }
    public int TargetHouseholdId { get; set; }
    public ConnectionStatus Status { get; set; } // Pending, Accepted, Rejected
    public DateTime CreatedAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public int RequestedByUserId { get; set; }
    public int? AcceptedByUserId { get; set; }
}

public enum ConnectionStatus
{
    Pending,
    Accepted,
    Rejected
}
```

**Pros:**
- Ongoing relationship for sharing
- Can share multiple recipes easily
- Could extend to meal plan sharing

**Cons:**
- More complex UI/UX
- Privacy concerns (browsing someone's recipes)
- Requires acceptance flow

### Option 3: Public Recipe Library

Mark recipes as "public" for a community library.

**Changes:**
```csharp
// Add to Recipe entity
public bool IsPublic { get; set; }
public string? PublicSlug { get; set; } // URL-safe unique identifier
```

**Pros:**
- Enables recipe discovery
- Could build community features

**Cons:**
- Significant scope increase
- Moderation concerns
- Privacy implications

## Recommended Implementation: Option 1 (Share Link)

### Phase 1: Basic Sharing (MVP)

1. **Database Migration**
   - Add `RecipeShareLinks` table

2. **Service Layer**
   - `RecipeShareService.CreateShareLinkAsync(recipeId)`
   - `RecipeShareService.GetRecipeByShareCodeAsync(shareCode)`
   - `RecipeShareService.ImportSharedRecipeAsync(shareCode, targetHouseholdId)`

3. **UI Components**
   - Add "Share" button to recipe card/detail view
   - Share modal showing link + copy button
   - `/share/{shareCode}` page for recipients
   - Import confirmation with preview

4. **Recipe Copy Logic**
   - Deep copy recipe + all ingredients
   - Add `SharedFromHouseholdId` and `SharedFromRecipeId` to track provenance
   - Clear `SourceUrl` if it was a scraped recipe (optional)

### Phase 2: Enhancements (Future)

- Share link expiration options (24h, 7d, never)
- View share analytics (who imported)
- Revoke share links
- QR code generation for in-person sharing
- Batch sharing (share multiple recipes at once)

## Data Model Changes

```csharp
// New entity
public class RecipeShareLink
{
    public int Id { get; set; }
    public int HouseholdId { get; set; }
    public int RecipeId { get; set; }
    public string ShareCode { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public int CreatedByUserId { get; set; }

    // Navigation
    public Household Household { get; set; } = default!;
    public Recipe Recipe { get; set; } = default!;
    public User CreatedBy { get; set; } = default!;
}

// Add to Recipe entity
public int? SharedFromHouseholdId { get; set; }
public int? SharedFromRecipeId { get; set; }
public string? SharedFromHouseholdName { get; set; } // Denormalized for display
```

## Security Considerations

1. **Share codes should be cryptographically random** (not sequential)
2. **Validate household membership** before creating share links
3. **Rate limit** share link creation
4. **Allow revoking** share links
5. **Don't expose household IDs** in share links

## Effort Estimate

- **Phase 1 (MVP):** 2-3 days
  - Database: 2 hours
  - Service layer: 4 hours
  - Share UI: 4 hours
  - Import page: 4 hours
  - Testing: 4 hours

- **Phase 2 (Enhancements):** 1-2 days

## Questions to Resolve

1. Should imported recipes maintain a link to the original (for updates)?
   - Recommendation: No, keep it simple. Copy is independent.

2. Should share links expire by default?
   - Recommendation: No expiration for MVP. Add as optional feature later.

3. Should we notify the original recipe owner when someone imports?
   - Recommendation: Not for MVP. Could add as setting later.

4. Should imported recipes be editable?
   - Recommendation: Yes, once imported it's fully owned by the recipient.
