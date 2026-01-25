namespace FamilyCoordinationApp.Services;

/// <summary>
/// Pub-sub notification service that decouples background polling from UI updates.
/// Components subscribe to events and call InvokeAsync(StateHasChanged) in handlers.
/// 
/// Security: All data change notifications include HouseholdId to enable tenant-scoped
/// updates. Subscribers should verify the householdId matches their context.
/// </summary>
public class DataNotifier
{
    /// <summary>
    /// Raised when recipes change. Parameter is the affected HouseholdId.
    /// </summary>
    public event Action<int>? OnRecipesChanged;
    
    /// <summary>
    /// Raised when shopping list changes. Parameter is the affected HouseholdId.
    /// </summary>
    public event Action<int>? OnShoppingListChanged;
    
    /// <summary>
    /// Raised when meal plan changes. Parameter is the affected HouseholdId.
    /// </summary>
    public event Action<int>? OnMealPlanChanged;
    
    /// <summary>
    /// Raised when presence information changes. Not household-scoped.
    /// </summary>
    public event Action? OnPresenceChanged;

    public void NotifyRecipesChanged(int householdId) => OnRecipesChanged?.Invoke(householdId);
    public void NotifyShoppingListChanged(int householdId) => OnShoppingListChanged?.Invoke(householdId);
    public void NotifyMealPlanChanged(int householdId) => OnMealPlanChanged?.Invoke(householdId);
    public void NotifyPresenceChanged() => OnPresenceChanged?.Invoke();
}
