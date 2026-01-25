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
/// 
/// Security: All queries are scoped by HouseholdId to prevent cross-tenant
/// information disclosure. Each household's changes are tracked independently.
/// </summary>
public class PollingService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    DataNotifier notifier,
    PresenceService presenceService,
    ILogger<PollingService> logger) : BackgroundService
{
    // Track last check times per household for tenant isolation
    private readonly Dictionary<int, DateTime> _lastShoppingListCheck = new();
    private readonly Dictionary<int, DateTime> _lastRecipeCheck = new();
    private readonly Dictionary<int, DateTime> _lastMealPlanCheck = new();
    private readonly object _lockObj = new();

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

        // Get all active households to check for changes
        var householdIds = await db.Households
            .Select(h => h.Id)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;

        foreach (var householdId in householdIds)
        {
            await CheckHouseholdChangesAsync(db, householdId, now, cancellationToken);
        }
    }

    private async Task CheckHouseholdChangesAsync(
        ApplicationDbContext db, 
        int householdId, 
        DateTime now, 
        CancellationToken ct)
    {
        DateTime lastShoppingCheck, lastRecipeCheck, lastMealPlanCheck;
        
        lock (_lockObj)
        {
            lastShoppingCheck = _lastShoppingListCheck.GetValueOrDefault(householdId, DateTime.MinValue);
            lastRecipeCheck = _lastRecipeCheck.GetValueOrDefault(householdId, DateTime.MinValue);
            lastMealPlanCheck = _lastMealPlanCheck.GetValueOrDefault(householdId, DateTime.MinValue);
        }

        // Check shopping list changes for this household only
        var hasShoppingChanges = await db.ShoppingListItems
            .AnyAsync(item => item.HouseholdId == householdId && item.UpdatedAt > lastShoppingCheck, cancellationToken);

        if (hasShoppingChanges)
        {
            logger.LogDebug("Shopping list changes detected for household {HouseholdId}", householdId);
            notifier.NotifyShoppingListChanged(householdId);
        }

        // Check recipe changes for this household only
        var hasRecipeChanges = await db.Recipes
            .AnyAsync(r => r.HouseholdId == householdId && r.UpdatedAt > lastRecipeCheck && !r.IsDeleted, cancellationToken);

        if (hasRecipeChanges)
        {
            logger.LogDebug("Recipe changes detected for household {HouseholdId}", householdId);
            notifier.NotifyRecipesChanged(householdId);
        }

        // Check meal plan changes for this household only
        var hasMealPlanChanges = await db.MealPlanEntries
            .AnyAsync(e => e.HouseholdId == householdId && e.UpdatedAt > lastMealPlanCheck, cancellationToken);

        if (hasMealPlanChanges)
        {
            logger.LogDebug("Meal plan changes detected for household {HouseholdId}", householdId);
            notifier.NotifyMealPlanChanged(householdId);
        }

        // Update timestamps for this household
        lock (_lockObj)
        {
            _lastShoppingListCheck[householdId] = now;
            _lastRecipeCheck[householdId] = now;
            _lastMealPlanCheck[householdId] = now;
        }
    }
}
