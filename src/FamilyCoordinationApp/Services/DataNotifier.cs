namespace FamilyCoordinationApp.Services;

/// <summary>
/// Pub-sub notification service that decouples background polling from UI updates.
/// Components subscribe to events and call InvokeAsync(StateHasChanged) in handlers.
/// </summary>
public class DataNotifier
{
    public event Action? OnRecipesChanged;
    public event Action? OnShoppingListChanged;
    public event Action? OnMealPlanChanged;
    public event Action? OnPresenceChanged;

    public void NotifyRecipesChanged() => OnRecipesChanged?.Invoke();
    public void NotifyShoppingListChanged() => OnShoppingListChanged?.Invoke();
    public void NotifyMealPlanChanged() => OnMealPlanChanged?.Invoke();
    public void NotifyPresenceChanged() => OnPresenceChanged?.Invoke();
}
