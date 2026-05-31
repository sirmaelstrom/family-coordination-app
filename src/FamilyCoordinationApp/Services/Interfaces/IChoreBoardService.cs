using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Services.Interfaces;

/// <summary>
/// Read/projection side of the chore board (WP-05). Assembles the single <see cref="ChoreBoardDto"/> that
/// every island lens groups client-side (D17/D18/M11) — chores with computed dueness/freshness, per-room
/// dirtiness rollups, the virtual General group, and the needs-attention ordering. Read-only: no writes,
/// no transitions (those are WP-04's <c>ChoreService</c>). All date math is delegated to
/// <see cref="ChoreStatusCalculator"/> (WP-02) using the injected <see cref="TimeZoneInfo"/> — never
/// <see cref="DateTime.UtcNow"/> reached inside (M5/D14).
/// </summary>
public interface IChoreBoardService
{
    /// <summary>
    /// Materialize the household board for the caller. Filters strictly by <paramref name="householdId"/>
    /// (M1, resolved server-side — never client-supplied). <paramref name="userId"/> identifies the caller
    /// so the caller's <c>User.ChoresDefaultView</c> is surfaced as <see cref="ChoreBoardDto.UserDefaultView"/>
    /// (the island opens onto the right lens without a second GET — D18/WP-12). <paramref name="now"/> may be
    /// supplied for deterministic tests; when null the service uses <c>TimeProvider.GetUtcNow()</c>.
    /// Chores with stored <c>Status</c> of Done or Archived are excluded from the board and the rollups.
    /// </summary>
    Task<ChoreBoardDto> GetBoardAsync(
        int householdId,
        int userId,
        DateTime? now = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Project a single <see cref="Chore"/> entity into its per-chore <see cref="ChoreDto"/>, computing the
    /// chore's dueness (<see cref="ChoreStatusCalculator.Compute"/>) and claim-staleness
    /// (<see cref="ChoreStatusCalculator.IsClaimStale"/>) as of <paramref name="now"/> in the configured
    /// timezone. Public + reusable so WP-06 can map the <see cref="Chore"/> that WP-04's mutating
    /// <c>ChoreService</c> methods return into the HTTP <see cref="ChoreDto"/> response WITHOUT a board
    /// rebuild — the board and the mutation responses share one projection (no DTO drift, M9).
    /// </summary>
    ChoreDto ProjectChore(Chore chore, DateTime now, TimeZoneInfo timeZone);
}
