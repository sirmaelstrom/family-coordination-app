using Microsoft.EntityFrameworkCore;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services;

public interface IMealPlanService
{
    Task<MealPlan> GetOrCreateMealPlanAsync(int householdId, DateOnly weekStart, CancellationToken ct = default);
    Task<MealPlanEntry> AddMealAsync(int householdId, DateOnly date, MealType mealType, int? recipeId, string? customMealName, string? notes = null, int? userId = null, CancellationToken ct = default);
    Task RemoveMealAsync(int householdId, int mealPlanId, int entryId, CancellationToken ct = default);
    DateOnly GetWeekStartDate(DateOnly date);
    DateOnly[] GetWeekDays(DateOnly weekStart);
}

public class MealPlanService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<MealPlanService> logger) : IMealPlanService
{

    public async Task<MealPlan> GetOrCreateMealPlanAsync(int householdId, DateOnly weekStart, CancellationToken ct = default)
    {
        // First, try to get existing meal plan (no retry needed for reads)
        await using (var readContext = await dbFactory.CreateDbContextAsync(ct))
        {
            var existingPlan = await readContext.MealPlans
                .Where(mp => mp.HouseholdId == householdId && mp.WeekStartDate == weekStart)
                .Include(mp => mp.Entries.OrderBy(e => e.Date).ThenBy(e => e.MealType))
                .ThenInclude(e => e.Recipe)
                .FirstOrDefaultAsync(ct);

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
                await using var context = await dbFactory.CreateDbContextAsync(ct);

                // Re-check if another request created the plan while we were retrying
                var existingPlan = await context.MealPlans
                    .Where(mp => mp.HouseholdId == householdId && mp.WeekStartDate == weekStart)
                    .Include(mp => mp.Entries.OrderBy(e => e.Date).ThenBy(e => e.MealType))
                    .ThenInclude(e => e.Recipe)
                    .FirstOrDefaultAsync(ct);

                if (existingPlan != null)
                {
                    return existingPlan;
                }

                var nextId = await GetNextMealPlanIdAsync(context, householdId, ct);
                var mealPlan = new MealPlan
                {
                    HouseholdId = householdId,
                    MealPlanId = nextId,
                    WeekStartDate = weekStart,
                    CreatedAt = DateTime.UtcNow
                };

                context.MealPlans.Add(mealPlan);
                await context.SaveChangesAsync(ct);

                logger.LogInformation("Created new meal plan {MealPlanId} for household {HouseholdId}, week {WeekStart}",
                    mealPlan.MealPlanId, householdId, weekStart);

                return mealPlan;
            },
            logger,
            "MealPlan");
    }

    public async Task<MealPlanEntry> AddMealAsync(int householdId, DateOnly date, MealType mealType, int? recipeId, string? customMealName, string? notes = null, int? userId = null, CancellationToken ct = default)
    {
        if (recipeId.HasValue && !string.IsNullOrWhiteSpace(customMealName))
        {
            throw new InvalidOperationException("Cannot specify both RecipeId and CustomMealName");
        }

        if (!recipeId.HasValue && string.IsNullOrWhiteSpace(customMealName))
        {
            throw new InvalidOperationException("Must specify either RecipeId or CustomMealName");
        }

        var weekStart = GetWeekStartDate(date);
        var mealPlan = await GetOrCreateMealPlanAsync(householdId, weekStart, ct);

        return await IdGenerationHelper.ExecuteWithRetryAsync(
            async (attempt) =>
            {
                await using var context = await dbFactory.CreateDbContextAsync(ct);

                // Check for duplicate entry (same date/mealType AND same recipe or custom meal)
                // This prevents adding the exact same recipe twice, but allows multiple different recipes
                var duplicateEntry = await context.MealPlanEntries
                    .FirstOrDefaultAsync(e =>
                        e.HouseholdId == householdId &&
                        e.MealPlanId == mealPlan.MealPlanId &&
                        e.Date == date &&
                        e.MealType == mealType &&
                        ((recipeId.HasValue && e.RecipeId == recipeId) ||
                         (!string.IsNullOrWhiteSpace(customMealName) && e.CustomMealName == customMealName)), ct);

                if (duplicateEntry != null)
                {
                    // Update existing entry with same recipe/custom meal (e.g., to update notes)
                    duplicateEntry.Notes = notes;
                    duplicateEntry.UpdatedAt = DateTime.UtcNow;
                    duplicateEntry.UpdatedByUserId = userId;

                    // Update parent MealPlan timestamp for polling
                    await UpdateMealPlanTimestampAsync(context, householdId, mealPlan.MealPlanId, ct);

                    await context.SaveChangesAsync(ct);

                    logger.LogInformation("Updated duplicate meal entry {EntryId} in plan {MealPlanId} for household {HouseholdId}",
                        duplicateEntry.EntryId, mealPlan.MealPlanId, householdId);

                    return duplicateEntry;
                }

                // Create new entry
                var nextEntryId = await GetNextEntryIdAsync(context, householdId, mealPlan.MealPlanId, ct);
                var entry = new MealPlanEntry
                {
                    HouseholdId = householdId,
                    MealPlanId = mealPlan.MealPlanId,
                    EntryId = nextEntryId,
                    Date = date,
                    MealType = mealType,
                    RecipeId = recipeId,
                    CustomMealName = customMealName,
                    Notes = notes
                };

                context.MealPlanEntries.Add(entry);

                // Update parent MealPlan timestamp for polling
                await UpdateMealPlanTimestampAsync(context, householdId, mealPlan.MealPlanId, ct);

                await context.SaveChangesAsync(ct);

                logger.LogInformation("Created meal entry {EntryId} in plan {MealPlanId} for household {HouseholdId}",
                    entry.EntryId, mealPlan.MealPlanId, householdId);

                return entry;
            },
            logger,
            "MealPlanEntry");
    }

    public async Task RemoveMealAsync(int householdId, int mealPlanId, int entryId, CancellationToken ct = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);

        var entry = await context.MealPlanEntries
            .FirstOrDefaultAsync(e =>
                e.HouseholdId == householdId &&
                e.MealPlanId == mealPlanId &&
                e.EntryId == entryId, ct);

        if (entry == null)
        {
            throw new InvalidOperationException($"Meal entry {entryId} not found in plan {mealPlanId} for household {householdId}");
        }

        // Hard delete
        context.MealPlanEntries.Remove(entry);

        // Update parent MealPlan timestamp for polling
        await UpdateMealPlanTimestampAsync(context, householdId, mealPlanId, ct);

        await context.SaveChangesAsync(ct);

        logger.LogInformation("Removed meal entry {EntryId} from plan {MealPlanId} for household {HouseholdId}",
            entryId, mealPlanId, householdId);
    }

    public DateOnly GetWeekStartDate(DateOnly date)
    {
        var daysFromMonday = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        return date.AddDays(-daysFromMonday);
    }

    public DateOnly[] GetWeekDays(DateOnly weekStart)
    {
        return Enumerable.Range(0, 7)
            .Select(i => weekStart.AddDays(i))
            .ToArray();
    }

    private static async Task<int> GetNextMealPlanIdAsync(ApplicationDbContext context, int householdId, CancellationToken ct)
    {
        var maxId = await context.MealPlans
            .Where(mp => mp.HouseholdId == householdId)
            .MaxAsync(mp => (int?)mp.MealPlanId, ct) ?? 0;

        return maxId + 1;
    }

    private static async Task<int> GetNextEntryIdAsync(ApplicationDbContext context, int householdId, int mealPlanId, CancellationToken ct)
    {
        var maxId = await context.MealPlanEntries
            .Where(e => e.HouseholdId == householdId && e.MealPlanId == mealPlanId)
            .MaxAsync(e => (int?)e.EntryId, ct) ?? 0;

        return maxId + 1;
    }

    private static async Task UpdateMealPlanTimestampAsync(ApplicationDbContext context, int householdId, int mealPlanId, CancellationToken ct)
    {
        var mealPlan = await context.MealPlans
            .FirstOrDefaultAsync(mp => mp.HouseholdId == householdId && mp.MealPlanId == mealPlanId, ct);

        if (mealPlan != null)
        {
            mealPlan.UpdatedAt = DateTime.UtcNow;
        }
    }
}
