using FamilyCoordinationApp.Data;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// Background service that polls for data changes and notifies subscribers.
/// Uses PeriodicTimer for clean async cancellation.
/// </summary>
public class PollingService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    DataNotifier notifier,
    PresenceService presenceService,
    ILogger<PollingService> logger) : BackgroundService
{
    private DateTime _lastShoppingListCheck = DateTime.UtcNow;
    private DateTime _lastRecipeCheck = DateTime.UtcNow;
    private DateTime _lastMealPlanCheck = DateTime.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("PollingService starting with 5-second interval");

        // Use 5-second interval for responsive collaboration
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckForChangesAsync(stoppingToken);
                presenceService.UpdatePresence();
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during polling check");
                // Continue polling even on errors
            }
        }

        logger.LogInformation("PollingService stopped");
    }

    private async Task CheckForChangesAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Check shopping list changes
        var hasShoppingChanges = await db.ShoppingListItems
            .AnyAsync(item => item.UpdatedAt > _lastShoppingListCheck, ct);

        if (hasShoppingChanges)
        {
            logger.LogDebug("Shopping list changes detected");
            notifier.NotifyShoppingListChanged();
        }
        _lastShoppingListCheck = DateTime.UtcNow;

        // Check recipe changes
        var hasRecipeChanges = await db.Recipes
            .AnyAsync(r => r.UpdatedAt > _lastRecipeCheck && !r.IsDeleted, ct);

        if (hasRecipeChanges)
        {
            logger.LogDebug("Recipe changes detected");
            notifier.NotifyRecipesChanged();
        }
        _lastRecipeCheck = DateTime.UtcNow;

        // Check meal plan changes
        var hasMealPlanChanges = await db.MealPlanEntries
            .AnyAsync(e => e.UpdatedAt > _lastMealPlanCheck, ct);

        if (hasMealPlanChanges)
        {
            logger.LogDebug("Meal plan changes detected");
            notifier.NotifyMealPlanChanged();
        }
        _lastMealPlanCheck = DateTime.UtcNow;
    }
}
