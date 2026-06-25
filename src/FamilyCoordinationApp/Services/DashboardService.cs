using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Dtos;
using FamilyCoordinationApp.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// Aggregates the dashboard island payload (strangler — mirrors <see cref="MealPlanBoardService"/>). Pure
/// composition over the existing domain services + a focused today-meals query; adds NO new business logic so
/// it rides those services' existing tests. The chore counts reuse the server-side, unit-tested
/// <see cref="ChoreHomeStats"/> reducer directly (same assembly) rather than re-implementing the snooze guard.
/// Read-only — never creates a plan/list/board row (an empty household yields zero counts / empty meals). All
/// reads create short-lived contexts via the factory and filter by <c>HouseholdId</c> (M1).
/// </summary>
public class DashboardService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IChoreBoardService choreBoardService,
    IShoppingListService shoppingListService,
    IMealPlanService mealPlanService) : IDashboardService
{
    public async Task<DashboardDto> GetDashboardAsync(
        int householdId,
        int userId,
        string greetingName,
        CancellationToken cancellationToken = default)
    {
        var householdName = await GetHouseholdNameAsync(userId, cancellationToken);

        // Chores: reuse the board read + the SERVER-side, unit-tested ChoreHomeStats reducer (incl. the
        // snooze guard on up-for-grabs). Parity with Home.razor's LoadChoreStats.
        var board = await choreBoardService.GetBoardAsync(householdId, userId, cancellationToken: cancellationToken);
        var choreStats = ChoreHomeStats.Compute(board.Chores);
        var chores = new DashboardChoreSummaryDto(
            choreStats.Total, choreStats.Overdue, choreStats.DueToday, choreStats.UpForGrabs);

        // Shopping: sum across ALL active (non-archived) lists — the household may have several going at once
        // and the card reflects everything still to buy (parity with Home.razor's LoadShoppingListStats).
        var lists = await shoppingListService.GetActiveShoppingListsAsync(householdId, cancellationToken);
        var allItems = lists.SelectMany(l => l.Items).ToList();
        var checkedCount = allItems.Count(i => i.IsChecked);
        var remaining = allItems.Count - checkedCount;
        var shopping = new DashboardShoppingSummaryDto(remaining, checkedCount, allItems.Count);

        // Today's meals: SERVER decides "today", queries that week's plan for today's entries only.
        var today = DateOnly.FromDateTime(DateTime.Today);
        var todaysMeals = await GetTodaysMealsAsync(householdId, today, cancellationToken);

        return new DashboardDto(greetingName, householdName, today, chores, shopping, todaysMeals);
    }

    private async Task<string> GetHouseholdNameAsync(int userId, CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        var name = await context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.Household != null ? u.Household.Name : null)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(name) ? "Your Household" : name;
    }

    private async Task<IReadOnlyList<DashboardMealDto>> GetTodaysMealsAsync(
        int householdId, DateOnly today, CancellationToken ct)
    {
        var weekStart = mealPlanService.GetWeekStartDate(today);

        await using var context = await dbFactory.CreateDbContextAsync(ct);
        var plan = await context.MealPlans
            .AsNoTracking()
            .Where(mp => mp.HouseholdId == householdId && mp.WeekStartDate == weekStart)
            .Include(mp => mp.Entries.Where(e => e.Date == today))
                .ThenInclude(e => e.Recipe)
            .FirstOrDefaultAsync(ct);

        if (plan is null)
        {
            return Array.Empty<DashboardMealDto>();
        }

        return plan.Entries
            .OrderBy(e => e.MealType)
            .Select(e => new DashboardMealDto(e.MealType, ResolveDisplayName(e)))
            .ToList();
    }

    // Parity with Home.razor's GetMealDisplayName: recipe name, else custom-meal name, else a placeholder.
    private static string ResolveDisplayName(MealPlanEntry meal)
    {
        if (meal.Recipe != null && !string.IsNullOrWhiteSpace(meal.Recipe.Name))
        {
            return meal.Recipe.Name;
        }
        if (!string.IsNullOrWhiteSpace(meal.CustomMealName))
        {
            return meal.CustomMealName;
        }
        return "Unnamed meal";
    }
}
