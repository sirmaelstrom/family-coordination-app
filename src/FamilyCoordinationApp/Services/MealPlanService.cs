using Microsoft.EntityFrameworkCore;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Services;

public class MealPlanService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<MealPlanService> logger,
    IHouseholdClock clock) : IMealPlanService
{

    public async Task<MealPlan> GetOrCreateMealPlanAsync(int householdId, DateOnly weekStart, CancellationToken cancellationToken = default)
    {
        // First, try to get existing meal plan (no retry needed for reads)
        await using (var readContext = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var existingPlan = await readContext.MealPlans
                .Where(mp => mp.WeekStartDate == weekStart)
                .Include(mp => mp.Entries.OrderBy(e => e.Date).ThenBy(e => e.MealType))
                .ThenInclude(e => e.Recipe)
                .FirstOrDefaultAsync(cancellationToken);

            if (existingPlan != null)
            {
                logger.LogInformation("Retrieved existing meal plan {MealPlanId} for household {HouseholdId}, week {WeekStart}",
                    existingPlan.MealPlanId, householdId, weekStart);
                return existingPlan;
            }
        }

        // Create new meal plan with retry logic for ID collisions
        return await IdGenerationHelper.ExecuteWithRetryAsync(
            async (attempt) =>
            {
                await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

                // Re-check if another request created the plan while we were retrying
                var existingPlan = await context.MealPlans
                    .Where(mp => mp.WeekStartDate == weekStart)
                    .Include(mp => mp.Entries.OrderBy(e => e.Date).ThenBy(e => e.MealType))
                    .ThenInclude(e => e.Recipe)
                    .FirstOrDefaultAsync(cancellationToken);

                if (existingPlan != null)
                {
                    return existingPlan;
                }

                var nextId = await GetNextMealPlanIdAsync(context, householdId, cancellationToken);
                var mealPlan = new MealPlan
                {
                    MealPlanId = nextId,
                    WeekStartDate = weekStart,
                    CreatedAt = clock.UtcNow
                };

                context.MealPlans.Add(mealPlan);
                await context.SaveChangesAsync(cancellationToken);

                logger.LogInformation("Created new meal plan {MealPlanId} for household {HouseholdId}, week {WeekStart}",
                    mealPlan.MealPlanId, householdId, weekStart);

                return mealPlan;
            },
            logger,
            "MealPlan");
    }

    public async Task<MealPlanEntry> AddMealAsync(int householdId, DateOnly date, MealType mealType, int? recipeId, string? customMealName, string? notes = null, int? userId = null, CancellationToken cancellationToken = default)
    {
        if (recipeId.HasValue && !string.IsNullOrWhiteSpace(customMealName))
        {
            throw new DomainValidationException("Cannot specify both RecipeId and CustomMealName");
        }

        if (!recipeId.HasValue && string.IsNullOrWhiteSpace(customMealName))
        {
            throw new DomainValidationException("Must specify either RecipeId or CustomMealName");
        }

        var weekStart = GetWeekStartDate(date);

        return await IdGenerationHelper.ExecuteWithRetryAsync(
            async (attempt) =>
            {
                await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

                // The week's plan is found or STAGED on this context, never committed on its own (quest b942b943):
                // a new week's plan and its first entry share the one SaveChanges below, so an entry insert that
                // fails (a bad recipe id's FK, say) leaves no empty plan behind. Two concurrent first adds for one
                // week collide on the plan id (and the retry finds the winner's plan) only when both read the max
                // before either commits; a rival that commits between this week read and the max read yields a
                // second plan for the week. Pre-existing (GetOrCreateMealPlanAsync has the same window); the fix
                // is a unique index on (HouseholdId, WeekStartDate), a migration (PR #134 Amendment 1).
                var mealPlan = await context.MealPlans
                    .FirstOrDefaultAsync(mp => mp.WeekStartDate == weekStart, cancellationToken);
                if (mealPlan == null)
                {
                    mealPlan = new MealPlan
                    {
                        MealPlanId = await GetNextMealPlanIdAsync(context, householdId, cancellationToken),
                        WeekStartDate = weekStart,
                        CreatedAt = clock.UtcNow
                    };
                    context.MealPlans.Add(mealPlan);
                }

                // Check for duplicate entry (same date/mealType AND same recipe or custom meal)
                // This prevents adding the exact same recipe twice, but allows multiple different recipes
                var duplicateEntry = await context.MealPlanEntries
                    .FirstOrDefaultAsync(e =>
                        e.MealPlanId == mealPlan.MealPlanId &&
                        e.Date == date &&
                        e.MealType == mealType &&
                        ((recipeId.HasValue && e.RecipeId == recipeId) ||
                         (!string.IsNullOrWhiteSpace(customMealName) && e.CustomMealName == customMealName)), cancellationToken);

                if (duplicateEntry != null)
                {
                    // Adding a meal that is ALREADY in this slot folds into the existing entry. This is the
                    // one entry write with no concurrency token — an add has no prior version to be stale —
                    // so it must not be able to destroy anything. Notes are therefore only written when the
                    // request actually carries them: an add that omits notes previously blanked whatever
                    // another member had typed here, with no conflict and no way to notice.
                    if (!string.IsNullOrWhiteSpace(notes))
                    {
                        duplicateEntry.Notes = notes;
                    }

                    duplicateEntry.UpdatedAt = clock.UtcNow;
                    duplicateEntry.UpdatedByUserId = userId;

                    // Update parent MealPlan timestamp for polling (the plan is tracked on this context).
                    mealPlan.UpdatedAt = clock.UtcNow;

                    await context.SaveChangesAsync(cancellationToken);

                    logger.LogInformation("Updated duplicate meal entry {EntryId} in plan {MealPlanId} for household {HouseholdId}",
                        duplicateEntry.EntryId, mealPlan.MealPlanId, householdId);

                    return duplicateEntry;
                }

                // Create new entry
                var nextEntryId = await GetNextEntryIdAsync(context, householdId, mealPlan.MealPlanId, cancellationToken);
                var entry = new MealPlanEntry
                {
                    MealPlanId = mealPlan.MealPlanId,
                    EntryId = nextEntryId,
                    Date = date,
                    MealType = mealType,
                    RecipeId = recipeId,
                    CustomMealName = customMealName,
                    Notes = notes,
                    CreatedByUserId = userId
                };

                context.MealPlanEntries.Add(entry);

                // Update parent MealPlan timestamp for polling. Set on the tracked plan rather than re-queried: a
                // plan staged above is not in the database yet, so a query would miss it.
                mealPlan.UpdatedAt = clock.UtcNow;

                await context.SaveChangesAsync(cancellationToken);

                logger.LogInformation("Created meal entry {EntryId} in plan {MealPlanId} for household {HouseholdId}",
                    entry.EntryId, mealPlan.MealPlanId, householdId);

                return entry;
            },
            logger,
            "MealPlanEntry");
    }

    public async Task<MealPlanEntry> MoveMealAsync(int householdId, int mealPlanId, int entryId, DateOnly newDate, MealType newMealType, uint version, int? userId = null, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Household-scoped — a cross-household id finds nothing ⇒ not found (M1). Recipe nav is loaded so
        // the caller can project the response without a second query.
        var entry = await context.MealPlanEntries
            .Include(e => e.Recipe)
            .FirstOrDefaultAsync(e =>
                e.MealPlanId == mealPlanId &&
                e.EntryId == entryId, cancellationToken);

        if (entry == null)
        {
            throw new NotFoundException(MealEntryNotFound, $"Meal entry {entryId} not found in plan {mealPlanId} for household {householdId}");
        }

        // Same-week only: a MealPlan owns exactly one week, so a date in another week would need a
        // cross-plan move (new EntryId under the other week's plan). The board UI only offers same-week
        // drops; reject anything else cleanly instead of corrupting the plan's week invariant.
        var mealPlan = await context.MealPlans
            .FirstOrDefaultAsync(mp => mp.MealPlanId == mealPlanId, cancellationToken);
        if (mealPlan == null || mealPlan.WeekStartDate != GetWeekStartDate(newDate))
        {
            throw new DomainValidationException("The target date is outside this meal plan's week.");
        }

        // Mirror the AddMealAsync duplicate guard: the same recipe / custom meal can't sit twice in one slot.
        var duplicateExists = await context.MealPlanEntries.AnyAsync(e =>
            e.MealPlanId == mealPlanId &&
            e.EntryId != entryId &&
            e.Date == newDate &&
            e.MealType == newMealType &&
            ((entry.RecipeId.HasValue && e.RecipeId == entry.RecipeId) ||
             (entry.CustomMealName != null && e.CustomMealName == entry.CustomMealName)), cancellationToken);
        if (duplicateExists)
        {
            throw new DomainValidationException("That meal is already planned in the target slot.");
        }

        entry.Date = newDate;
        entry.MealType = newMealType;
        entry.UpdatedAt = clock.UtcNow;
        entry.UpdatedByUserId = userId;

        // Update parent MealPlan timestamp for polling
        mealPlan.UpdatedAt = clock.UtcNow;

        await context.SaveWithConcurrencyAsync(entry, version, cancellationToken);

        logger.LogInformation("Moved meal entry {EntryId} in plan {MealPlanId} for household {HouseholdId} to {Date} {MealType}",
            entryId, mealPlanId, householdId, newDate, newMealType);

        return entry;
    }

    private const string MealEntryNotFound = "Meal entry not found.";

    /// <summary>Upper bound on a per-entry servings override; mirrored client-side in lib/servings.ts.</summary>
    public const int MaxServings = 1000;

    public async Task<MealPlanEntry> SetMealServingsAsync(
        int householdId,
        int mealPlanId,
        int entryId,
        int? servings,
        uint version,
        int? userId = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Household-scoped — a cross-household id finds nothing ⇒ not found (M1). Recipe nav is loaded so the
        // caller can project the response without a second query (parity MoveMealAsync).
        var entry = await context.MealPlanEntries
            .Include(e => e.Recipe)
            .FirstOrDefaultAsync(e =>
                e.MealPlanId == mealPlanId &&
                e.EntryId == entryId, cancellationToken);

        if (entry == null)
        {
            throw new NotFoundException(MealEntryNotFound, $"Meal entry {entryId} not found in plan {mealPlanId} for household {householdId}");
        }

        if (servings is <= 0)
        {
            throw new DomainValidationException("Servings must be a positive number, or null to cook the recipe as written.");
        }

        // Bounded because the value becomes a MULTIPLIER on every ingredient of that meal, and
        // ShoppingListItem.Quantity is decimal(10,2) — an unbounded servings count turns a generate into a
        // numeric-overflow 500 rather than a silly list. 1000 is far past any real household.
        if (servings > MaxServings)
        {
            throw new DomainValidationException($"Servings must be {MaxServings} or fewer.");
        }

        entry.Servings = servings;
        entry.UpdatedAt = clock.UtcNow;
        entry.UpdatedByUserId = userId;

        // Update parent MealPlan timestamp for polling (parity MoveMealAsync — the board polls on it).
        var mealPlan = await context.MealPlans
            .FirstOrDefaultAsync(mp => mp.MealPlanId == mealPlanId, cancellationToken);
        if (mealPlan != null)
        {
            mealPlan.UpdatedAt = clock.UtcNow;
        }

        await context.SaveWithConcurrencyAsync(entry, version, cancellationToken);

        logger.LogInformation("Set servings to {Servings} on meal entry {EntryId} in plan {MealPlanId} for household {HouseholdId}",
            servings, entryId, mealPlanId, householdId);

        return entry;
    }

    public async Task RemoveMealAsync(int householdId, int mealPlanId, int entryId, uint version, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var entry = await context.MealPlanEntries
            .FirstOrDefaultAsync(e =>
                e.MealPlanId == mealPlanId &&
                e.EntryId == entryId, cancellationToken);

        if (entry == null)
        {
            throw new NotFoundException(MealEntryNotFound, $"Meal entry {entryId} not found in plan {mealPlanId} for household {householdId}");
        }

        // Hard delete
        context.MealPlanEntries.Remove(entry);

        // Update parent MealPlan timestamp for polling
        await UpdateMealPlanTimestampAsync(context, householdId, mealPlanId, cancellationToken);

        await context.SaveWithConcurrencyAsync(entry, version, cancellationToken);

        logger.LogInformation("Removed meal entry {EntryId} from plan {MealPlanId} for household {HouseholdId}",
            entryId, mealPlanId, householdId);
    }

    public DateOnly GetWeekStartDate(DateOnly date)
    {
        var daysFromMonday = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        return date.AddDays(-daysFromMonday);
    }

    private static async Task<int> GetNextMealPlanIdAsync(ApplicationDbContext context, int householdId, CancellationToken cancellationToken)
    {
        var maxId = await context.MealPlans
            .MaxAsync(mp => (int?)mp.MealPlanId, cancellationToken) ?? 0;

        return maxId + 1;
    }

    private static async Task<int> GetNextEntryIdAsync(ApplicationDbContext context, int householdId, int mealPlanId, CancellationToken cancellationToken)
    {
        var maxId = await context.MealPlanEntries
            .Where(e => e.MealPlanId == mealPlanId)
            .MaxAsync(e => (int?)e.EntryId, cancellationToken) ?? 0;

        return maxId + 1;
    }

    private async Task UpdateMealPlanTimestampAsync(ApplicationDbContext context, int householdId, int mealPlanId, CancellationToken cancellationToken)
    {
        var mealPlan = await context.MealPlans
            .FirstOrDefaultAsync(mp => mp.MealPlanId == mealPlanId, cancellationToken);

        if (mealPlan != null)
        {
            mealPlan.UpdatedAt = clock.UtcNow;
        }
    }
}
