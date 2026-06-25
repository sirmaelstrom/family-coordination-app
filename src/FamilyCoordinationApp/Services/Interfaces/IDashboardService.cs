using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Services.Interfaces;

/// <summary>
/// Read + projection for the dashboard island aggregate (strangler — mirrors <c>IMealPlanBoardService</c>).
/// Assembles the single <see cref="DashboardDto"/> by composing the existing domain services — the chore
/// summary via <c>ChoreHomeStats.Compute</c> over <see cref="IChoreBoardService"/>, the shopping summary via
/// <see cref="IShoppingListService"/>, and today's meals via a focused household-scoped query. Read-only: no
/// writes, no row creation (an empty household yields zero counts / empty lists). All reads filter by
/// <c>HouseholdId</c> (M1, resolved server-side — never client-supplied).
/// </summary>
public interface IDashboardService
{
    /// <summary>
    /// Build the dashboard for the caller. <paramref name="greetingName"/> is resolved from the caller's
    /// claims by the endpoint (so this service stays claims-free + unit-testable); everything else is read
    /// from <paramref name="householdId"/> / <paramref name="userId"/>. Never creates a meal plan, list, or
    /// board row.
    /// </summary>
    Task<DashboardDto> GetDashboardAsync(
        int householdId,
        int userId,
        string greetingName,
        CancellationToken cancellationToken = default);
}
