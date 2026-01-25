using FamilyCoordinationApp.Data;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// Sync status for UI display
/// </summary>
public enum SyncStatus
{
    Synced,
    Syncing,
    Offline,
    Error
}

/// <summary>
/// Background service that polls for data changes and notifies subscribers.
/// Uses PeriodicTimer for clean async cancellation.
/// Exposes sync status for UI indicators.
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

    /// <summary>
    /// Current sync status
    /// </summary>
    public SyncStatus Status { get; private set; } = SyncStatus.Synced;

    /// <summary>
    /// Last successful sync time
    /// </summary>
    public DateTime? LastSyncTime { get; private set; }

    /// <summary>
    /// Last error message (if Status == Error)
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Number of consecutive errors
    /// </summary>
    public int ConsecutiveErrors { get; private set; }

    /// <summary>
    /// Event raised when sync status changes
    /// </summary>
    public event Action? OnStatusChanged;

    private void SetStatus(SyncStatus newStatus, string? error = null)
    {
        var changed = Status != newStatus || LastError != error;
        Status = newStatus;
        LastError = error;

        if (newStatus == SyncStatus.Synced)
        {
            LastSyncTime = DateTime.UtcNow;
            ConsecutiveErrors = 0;
        }
        else if (newStatus == SyncStatus.Error)
        {
            ConsecutiveErrors++;
        }

        if (changed)
        {
            OnStatusChanged?.Invoke();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("PollingService starting with 5-second interval");

        // Use 5-second interval for responsive collaboration
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                SetStatus(SyncStatus.Syncing);
                await CheckForChangesAsync(stoppingToken);
                presenceService.UpdatePresence();
                SetStatus(SyncStatus.Synced);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
                break;
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Network error during polling - likely offline");
                SetStatus(SyncStatus.Offline, "No network connection");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during polling check");
                SetStatus(SyncStatus.Error, ex.Message);
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
