using FamilyCoordinationApp.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Data;

public static class SeedData
{
    public static async Task SeedDevelopmentDataAsync(IDbContextFactory<ApplicationDbContext> dbFactory)
    {
        using var context = dbFactory.CreateDbContext();

        // Only seed if no recipes exist
        if (await context.Recipes.AnyAsync())
            return;

        var household = await context.Households.FirstOrDefaultAsync();
        if (household == null)
            return;

        var user = await context.Users.FirstOrDefaultAsync(u => u.HouseholdId == household.Id);
        if (user == null)
            return;

        // Seed default categories
        await SeedDefaultCategoriesAsync(dbFactory, household.Id);

        // Seed the curated chore/room library (idempotent — no-op if rooms/chores already exist).
        await SeedChoresAndRoomsAsync(dbFactory, household.Id);

        // Sample recipes with realistic ingredients
        var sampleRecipes = GetSampleRecipes();
        var recipeId = 1;

        foreach (var (name, description, servings, prepTime, cookTime, ingredients) in sampleRecipes)
        {
            var recipe = new Recipe
            {
                HouseholdId = household.Id,
                RecipeId = recipeId++,
                Name = name,
                Description = description,
                Instructions = $"1. Prepare all ingredients\n2. Follow standard cooking method for {name.ToLower()}\n3. Serve and enjoy",
                Servings = servings,
                PrepTimeMinutes = prepTime,
                CookTimeMinutes = cookTime,
                CreatedByUserId = user.Id,
                CreatedAt = DateTime.UtcNow.AddDays(-recipeId)
            };

            var ingredientId = 1;
            foreach (var (ingName, qty, unit, category) in ingredients)
            {
                recipe.Ingredients.Add(new RecipeIngredient
                {
                    HouseholdId = household.Id,
                    RecipeId = recipe.RecipeId,
                    IngredientId = ingredientId,
                    Name = ingName,
                    Quantity = qty,
                    Unit = unit,
                    Category = category,
                    SortOrder = ingredientId++
                });
            }

            context.Recipes.Add(recipe);
        }

        await context.SaveChangesAsync();
    }

    private static List<(string Name, string Description, int Servings, int PrepTime, int CookTime,
        List<(string Name, decimal Qty, string Unit, string Category)> Ingredients)> GetSampleRecipes()
    {
        return new()
        {
            ("Spaghetti Bolognese", "Classic Italian meat sauce over pasta", 4, 15, 45, new()
            {
                ("Ground Beef", 1m, "lb", "Meat"),
                ("Spaghetti", 1m, "lb", "Pantry"),
                ("Crushed Tomatoes", 28m, "oz", "Pantry"),
                ("Onion", 1m, "piece", "Produce"),
                ("Garlic", 3m, "clove", "Produce"),
                ("Olive Oil", 2m, "tbsp", "Pantry")
            }),
            ("Chicken Stir Fry", "Quick and healthy Asian-inspired dish", 4, 20, 15, new()
            {
                ("Chicken Breast", 1.5m, "lb", "Meat"),
                ("Bell Peppers", 2m, "piece", "Produce"),
                ("Broccoli", 2m, "cup", "Produce"),
                ("Soy Sauce", 3m, "tbsp", "Pantry"),
                ("Sesame Oil", 1m, "tbsp", "Pantry"),
                ("Ginger", 1m, "tbsp", "Produce")
            }),
            ("Beef Tacos", "Seasoned ground beef in crispy shells", 6, 10, 20, new()
            {
                ("Ground Beef", 1m, "lb", "Meat"),
                ("Taco Shells", 12m, "piece", "Pantry"),
                ("Cheddar Cheese", 1m, "cup", "Dairy"),
                ("Lettuce", 2m, "cup", "Produce"),
                ("Tomato", 2m, "piece", "Produce"),
                ("Taco Seasoning", 1m, "packet", "Spices")
            }),
            ("Grilled Salmon", "Simple herb-crusted salmon fillets", 4, 10, 15, new()
            {
                ("Salmon Fillets", 2m, "lb", "Meat"),
                ("Lemon", 1m, "piece", "Produce"),
                ("Dill", 2m, "tbsp", "Spices"),
                ("Olive Oil", 2m, "tbsp", "Pantry"),
                ("Garlic", 2m, "clove", "Produce")
            }),
            ("Caesar Salad", "Crisp romaine with classic dressing", 4, 15, 0, new()
            {
                ("Romaine Lettuce", 2m, "head", "Produce"),
                ("Parmesan Cheese", 0.5m, "cup", "Dairy"),
                ("Croutons", 1m, "cup", "Pantry"),
                ("Caesar Dressing", 0.5m, "cup", "Dairy"),
                ("Lemon", 1m, "piece", "Produce")
            })
        };
    }

    /// <summary>
    /// Seeds a curated, realistic chore/room library for a household so that locally — on a fresh
    /// install or a newly-created household — the board exercises every recurrence mode, every
    /// assignment state, every effort tier, and decay/freshness coloring (D15/V10).
    ///
    /// Idempotent: if the household already has any rooms OR any chores, this is a no-op. Backdated
    /// completion history is written DIRECTLY via the context (council M13) — never through
    /// <c>ChoreService.CompleteAsync</c>, which stamps "now" and cannot backdate. The seed user (the
    /// household's first user) is the entered-by/owner and the holder of any seeded assignment; at
    /// household-creation time there is exactly one user, so assignment states necessarily point at
    /// that single member (council noted).
    /// </summary>
    public static async Task SeedChoresAndRoomsAsync(IDbContextFactory<ApplicationDbContext> dbFactory, int householdId)
    {
        await using var context = await dbFactory.CreateDbContextAsync();

        // Idempotency guard (M1-scoped to this household): skip entirely if rooms OR chores already
        // exist. Mirrors the "only seed when empty" style of SeedDefaultCategoriesAsync.
        var alreadySeeded = await context.Rooms.AnyAsync(r => r.HouseholdId == householdId)
            || await context.Chores.AnyAsync(c => c.HouseholdId == householdId);

        if (alreadySeeded) return;

        // Resolve the seed user (the household's first user) for authorship/ownership/assignment.
        // Without a user we cannot satisfy the required EnteredByUserId FK, so bail gracefully.
        var seedUserId = await context.Users
            .Where(u => u.HouseholdId == householdId)
            .OrderBy(u => u.Id)
            .Select(u => (int?)u.Id)
            .FirstOrDefaultAsync();

        if (seedUserId is not { } userId) return;

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);

        // ---- Rooms: several named areas; "General" is the virtual roomless group (no Room row). ----
        // RoomIds are assigned per-household starting at 1 (the household is empty here); SortOrder
        // appends, mirroring RoomService.CreateRoomAsync (max+1).
        var rooms = new[]
        {
            new Room { HouseholdId = householdId, RoomId = 1, Name = "Kitchen", Icon = "🍳", SortOrder = 1, CreatedAt = now },
            new Room { HouseholdId = householdId, RoomId = 2, Name = "Bathroom", Icon = "🛁", SortOrder = 2, CreatedAt = now },
            new Room { HouseholdId = householdId, RoomId = 3, Name = "Bedroom", Icon = "🛏️", SortOrder = 3, CreatedAt = now },
            new Room { HouseholdId = householdId, RoomId = 4, Name = "Living Room", Icon = "🛋️", SortOrder = 4, CreatedAt = now },
            new Room { HouseholdId = householdId, RoomId = 5, Name = "Yard", Icon = "🌳", SortOrder = 5, CreatedAt = now }
        };
        context.Rooms.AddRange(rooms);

        const int kitchen = 1;
        const int bathroom = 2;
        const int bedroom = 3;
        const int livingRoom = 4;
        const int yard = 5;

        var choreId = 0;
        var completionId = 0;

        // Local helper: build a chore with the invariant trio + effort tier → points kept consistent
        // (P3 — never hand-type points that disagree with the tier; ChoreEffort.PointsFor is the SoT).
        Chore NewChore(
            string name,
            int? roomId,
            RecurrenceMode recurrence,
            EffortTier effort,
            string? description = null,
            int? intervalDays = null,
            DateOnly? anchorDate = null,
            ChoreDaysOfWeek? daysOfWeek = null,
            DateTime? lastCompletedAt = null,
            int? ownerUserId = null,
            int? assigneeUserId = null,
            AssignmentKind assignmentKind = AssignmentKind.None,
            DateTime? claimedAt = null)
        {
            var chore = new Chore
            {
                HouseholdId = householdId,
                ChoreId = ++choreId,
                Name = name,
                Description = description,
                RoomId = roomId,
                RecurrenceMode = recurrence,
                IntervalDays = intervalDays,
                AnchorDate = anchorDate,
                DaysOfWeek = daysOfWeek,
                DayOfMonth = null, // monthly-on-day is intentionally unsupported (D4-B / E5).
                LastCompletedAt = lastCompletedAt,
                EffortTier = effort,
                EffortPoints = ChoreEffort.PointsFor(effort),
                Status = ChoreStatus.Active,
                EnteredByUserId = userId,
                OwnerUserId = ownerUserId,
                AssigneeUserId = assigneeUserId,
                AssignmentKind = assignmentKind,
                ClaimedAt = claimedAt,
                CreatedAt = now
            };

            // If the chore was last completed, log a matching backdated completion so the decay band
            // and completion history render (failure-criteria: LastCompletedAt without a row, or a row
            // without LastCompletedAt). Written directly here — NOT via ChoreService (M13).
            if (lastCompletedAt is { } completedAt)
            {
                chore.Completions.Add(new ChoreCompletion
                {
                    HouseholdId = householdId,
                    ChoreId = chore.ChoreId,
                    CompletionId = ++completionId,
                    CompletedByUserId = userId,
                    CompletedAt = completedAt,
                    EffortPointsSnapshot = chore.EffortPoints,
                    Note = "Seeded completion"
                });
            }

            return chore;
        }

        var chores = new List<Chore>
        {
            // ---- Flexible (decay relative to last completion) — exercises Fresh / Mid / Overdue. ----
            // Fresh: completed today → fraction 0 → Fresh / NotDue.
            NewChore("Wipe down counters", kitchen, RecurrenceMode.Flexible, EffortTier.Quick,
                description: "Clear and wipe the kitchen counters.",
                intervalDays: 3, lastCompletedAt: now),

            // Mid: ~5 days into a 7-day cadence → fraction ~0.71 → Mid / NotDue.
            NewChore("Vacuum living room", livingRoom, RecurrenceMode.Flexible, EffortTier.Standard,
                description: "Vacuum carpets and rugs.",
                intervalDays: 7, lastCompletedAt: now.AddDays(-5)),

            // Overdue: 12 days into a 7-day cadence → fraction > 1 → Overdue.
            NewChore("Clean bathroom", bathroom, RecurrenceMode.Flexible, EffortTier.Standard,
                description: "Scrub sink, toilet, and shower.",
                intervalDays: 7, lastCompletedAt: now.AddDays(-12)),

            // Flexible never completed → first-occurrence pressure → DueToday / Due.
            NewChore("Mop kitchen floor", kitchen, RecurrenceMode.Flexible, EffortTier.BigJob,
                description: "Sweep then mop the whole floor.",
                intervalDays: 14),

            // ---- Fixed weekly-on-weekday (D4-B) — DueToday on a flagged weekday else Scheduled. ----
            NewChore("Take out trash", null, RecurrenceMode.Fixed, EffortTier.Quick,
                description: "Roll bins to the curb.",
                daysOfWeek: ChoreDaysOfWeek.Monday | ChoreDaysOfWeek.Thursday),

            NewChore("Water the plants", livingRoom, RecurrenceMode.Fixed, EffortTier.Quick,
                description: "Water indoor and porch plants.",
                daysOfWeek: ChoreDaysOfWeek.Wednesday | ChoreDaysOfWeek.Sunday),

            // ---- Fixed every-N (anchor + interval) — DueToday on a cadence multiple else Scheduled. ----
            NewChore("Mow the lawn", yard, RecurrenceMode.Fixed, EffortTier.BigJob,
                description: "Mow front and back yard.",
                intervalDays: 10, anchorDate: today.AddDays(-20)),

            // ---- OneOff (due against AnchorDate, never recurs). ----
            // Overdue one-off (anchor in the past, not completed).
            NewChore("Return library books", null, RecurrenceMode.OneOff, EffortTier.Quick,
                description: "Drop the overdue books at the branch.",
                anchorDate: today.AddDays(-2)),

            // Future one-off (anchor ahead → NotDue / Fresh).
            NewChore("Schedule HVAC service", null, RecurrenceMode.OneOff, EffortTier.Standard,
                description: "Book the seasonal furnace tune-up.",
                anchorDate: today.AddDays(7)),

            // ---- Assignment states (limited to the single seed member at creation time). ----
            // Assigned (sticky): AssignmentKind.Assigned + assignee + null ClaimedAt (deliberate
            // assignment is never auto-released — claimedAt stays null for Assigned).
            NewChore("Manage weekly meal plan", kitchen, RecurrenceMode.Flexible, EffortTier.Standard,
                description: "Plan meals and update the shopping list.",
                intervalDays: 7, lastCompletedAt: now.AddDays(-2),
                ownerUserId: userId, assigneeUserId: userId, assignmentKind: AssignmentKind.Assigned),

            // Claimed (fresh, NOT stale): AssignmentKind.Claimed + assignee + recent ClaimedAt
            // (well within the 48h staleness threshold → isClaimStale == false).
            NewChore("Make the beds", bedroom, RecurrenceMode.Fixed, EffortTier.Quick,
                description: "Make all the beds.",
                daysOfWeek: ChoreDaysOfWeek.Saturday | ChoreDaysOfWeek.Sunday,
                assigneeUserId: userId, assignmentKind: AssignmentKind.Claimed, claimedAt: now.AddHours(-2))

            // The remaining chores above leave the assignment trio at its default Unclaimed-pile state
            // (AssigneeUserId == null, AssignmentKind.None, ClaimedAt == null).
        };

        context.Chores.AddRange(chores);

        await context.SaveChangesAsync();
    }

    public static async Task SeedDefaultCategoriesAsync(IDbContextFactory<ApplicationDbContext> dbFactory, int householdId)
    {
        await using var context = dbFactory.CreateDbContext();

        // Check if categories already exist for this household
        var existingCount = await context.Categories
            .IgnoreQueryFilters()  // Include soft-deleted
            .CountAsync(c => c.HouseholdId == householdId);

        if (existingCount > 0) return;

        var defaultCategories = new[]
        {
            new Category { HouseholdId = householdId, CategoryId = 1, Name = "Meat", IconEmoji = "meat_on_bone", Color = "#b71c1c", SortOrder = 1, IsDefault = true },
            new Category { HouseholdId = householdId, CategoryId = 2, Name = "Produce", IconEmoji = "leafy_green", Color = "#2e7d32", SortOrder = 2, IsDefault = true },
            new Category { HouseholdId = householdId, CategoryId = 3, Name = "Dairy", IconEmoji = "cheese_wedge", Color = "#ffc107", SortOrder = 3, IsDefault = true },
            new Category { HouseholdId = householdId, CategoryId = 4, Name = "Pantry", IconEmoji = "canned_food", Color = "#795548", SortOrder = 4, IsDefault = true },
            new Category { HouseholdId = householdId, CategoryId = 5, Name = "Spices", IconEmoji = "hot_pepper", Color = "#ff5722", SortOrder = 5, IsDefault = true },
            new Category { HouseholdId = householdId, CategoryId = 6, Name = "Frozen", IconEmoji = "snowflake", Color = "#03a9f4", SortOrder = 6, IsDefault = true },
            new Category { HouseholdId = householdId, CategoryId = 7, Name = "Bakery", IconEmoji = "bread", Color = "#8d6e63", SortOrder = 7, IsDefault = true },
            new Category { HouseholdId = householdId, CategoryId = 8, Name = "Beverages", IconEmoji = "cup_with_straw", Color = "#9c27b0", SortOrder = 8, IsDefault = true },
            new Category { HouseholdId = householdId, CategoryId = 9, Name = "Other", IconEmoji = "package", Color = "#607d8b", SortOrder = 9, IsDefault = true }
        };

        context.Categories.AddRange(defaultCategories);
        await context.SaveChangesAsync();
    }
}
