---
phase: 05-multi-user-collaboration
plan: 01
subsystem: data-layer
status: complete
tags: [postgresql, concurrency, entity-framework, xmin, optimistic-locking]

requires:
  - "04-06: Docker deployment with data persistence"
  - "01-01: Entity framework schema with composite keys"

provides:
  - "PostgreSQL xmin-based optimistic concurrency for collaborative entities"
  - "User profile picture URL and initials for avatar display"
  - "Change tracking fields (UpdatedAt, UpdatedByUserId) for polling detection"

affects:
  - "05-02+: All future collaboration features will use these concurrency tokens"
  - "Phase 6: Real-time sync will use UpdatedAt for change polling"

tech-stack:
  added:
    - "System.ComponentModel.DataAnnotations ([Timestamp] attribute)"
  patterns:
    - "PostgreSQL xmin concurrency token (uint with [Timestamp] attribute)"
    - "IsRowVersion() fluent API configuration for xmin mapping"
    - "Nullable UpdatedByUserId with SetNull cascade behavior"

key-files:
  created:
    - "Migrations/20260124192211_AddCollaborationFields.cs"
    - "Migrations/20260124192211_AddCollaborationFields.Designer.cs"
  modified:
    - "Data/Entities/User.cs"
    - "Data/Entities/Recipe.cs"
    - "Data/Entities/ShoppingListItem.cs"
    - "Data/Entities/MealPlanEntry.cs"
    - "Data/Configurations/RecipeConfiguration.cs"
    - "Data/Configurations/ShoppingListItemConfiguration.cs"
    - "Data/Configurations/MealPlanEntryConfiguration.cs"

decisions:
  - decision: "Use PostgreSQL xmin system column for concurrency detection"
    rationale: "Native database support, automatic maintenance, no manual versioning"
    alternatives: "RowVersion timestamp, manual version incrementing"
    impact: "Zero application overhead for concurrency token management"
    date: 2026-01-24

  - decision: "Store user profile picture URL (not binary data)"
    rationale: "Google OAuth provides picture URL, no storage overhead, simplifies serving"
    alternatives: "Store binary in database, store in filesystem"
    impact: "Relies on Google CDN availability"
    date: 2026-01-24

  - decision: "Store computed Initials field (not computed column)"
    rationale: "Simple string field, computed in application when user created/updated"
    alternatives: "PostgreSQL computed column, client-side computation only"
    impact: "Requires application logic to maintain, but more flexible"
    date: 2026-01-24

  - decision: "Nullable UpdatedByUserId with SetNull cascade"
    rationale: "Preserves change history even if user deleted, prevents cascade failures"
    alternatives: "Required field with Restrict, anonymous user placeholder"
    impact: "Historical records may have null UpdatedBy"
    date: 2026-01-24

metrics:
  duration: 2.7min
  completed: 2026-01-24
---

# Phase 5 Plan 1: Multi-User Schema Foundations Summary

**One-liner:** PostgreSQL xmin concurrency tokens and user profile fields for optimistic locking and avatar display.

## What Was Built

Added schema foundation for multi-user collaboration:

1. **User Profile Fields:**
   - `PictureUrl` (string, nullable) - Google OAuth profile picture URL
   - `Initials` (string, required) - Computed initials for fallback avatar display

2. **Concurrency Tokens (xmin-based):**
   - `Version` (uint) with `[Timestamp]` attribute on Recipe, ShoppingListItem, MealPlanEntry
   - Maps to PostgreSQL `xmin` system column via `IsRowVersion()` configuration
   - Enables optimistic concurrency conflict detection

3. **Change Tracking Fields:**
   - `UpdatedAt` (DateTime, nullable) - Timestamp of last modification
   - `UpdatedByUserId` (int, nullable) - Foreign key to User
   - `UpdatedBy` navigation property - User who made last change
   - Added to Recipe (already had UpdatedAt), ShoppingListItem, MealPlanEntry

## Migration Structure

**AddCollaborationFields Migration:**
- Adds `PictureUrl` and `Initials` columns to Users table
- Adds `UpdatedAt`, `UpdatedByUserId`, and `xmin` columns to Recipes, ShoppingListItems, MealPlanEntries
- Creates foreign key indexes for UpdatedByUserId
- Configures SetNull cascade behavior (preserves history if user deleted)

**Database Impact:**
- Users: +2 columns (PictureUrl, Initials)
- Recipes: +2 columns (UpdatedByUserId, xmin - UpdatedAt already existed)
- ShoppingListItems: +3 columns (UpdatedAt, UpdatedByUserId, xmin)
- MealPlanEntries: +3 columns (UpdatedAt, UpdatedByUserId, xmin)

## Technical Implementation

**PostgreSQL xmin Integration:**
```csharp
// Entity property
[Timestamp]
public uint Version { get; set; }

// EF Core configuration
builder.Property(e => e.Version).IsRowVersion();
```

The `[Timestamp]` attribute on a `uint` property in Npgsql automatically maps to PostgreSQL's `xmin` system column. The `IsRowVersion()` call is optional but makes the intent explicit.

**Foreign Key Configuration Pattern:**
```csharp
builder.HasOne(e => e.UpdatedBy)
    .WithMany()
    .HasForeignKey(e => e.UpdatedByUserId)
    .OnDelete(DeleteBehavior.SetNull)
    .IsRequired(false);
```

**Why xmin vs RowVersion:**
- PostgreSQL doesn't have SQL Server's ROWVERSION type
- xmin is a system column maintained by PostgreSQL for MVCC (transaction ID)
- Changes on every UPDATE automatically
- Zero storage overhead (already exists in every row)
- Perfect for optimistic concurrency detection

## Deviations from Plan

None - plan executed exactly as written.

## Verification Results

**Build:** Clean build with no errors or warnings.

**Migration:** Successfully created `20260124192211_AddCollaborationFields.cs`.

**Grep Verification:**
- `[Timestamp]` attribute found in Recipe, ShoppingListItem, MealPlanEntry
- `PictureUrl` and `Initials` properties found in User entity
- All foreign key relationships configured

## Next Phase Readiness

**Ready for 05-02 (Conflict Detection Service):**
- Version tokens available for DbUpdateConcurrencyException detection
- UpdatedAt/UpdatedByUserId available for UI display of who/when changed
- User profile fields ready for avatar display in conflict resolution UI

**Integration Points:**
- SaveChangesAsync will throw DbUpdateConcurrencyException when xmin mismatch
- Service layer can catch exception and present conflict to user
- UI can display UpdatedBy.DisplayName and UpdatedAt in conflict dialogs

**No blockers identified.**

---
*Completed: 2026-01-24T19:23:01Z*
*Commit: e175199*
