using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// Shared read/write helpers for the ChoreRoom join — a chore's 0..N room memberships (Phase 13).
/// The three later WPs (board read, digest fan-out, write reconcile) all go through here so no
/// downstream WP re-invents membership access.
///
/// Load-bearing invariants (council review-1):
/// <list type="bullet">
///   <item>Every query filters by <c>HouseholdId</c> (M1 — multi-tenant security boundary).</item>
///   <item>Reads return roomIds sorted ascending so the board's <c>roomIds[]</c> wire is
///   deterministic (otherwise the byte-equality contract fixture is flaky).</item>
///   <item>Writes <c>.Distinct()</c>-normalize the desired set (a duplicate id would violate the
///   composite PK on <c>roomIds:[10,10]</c>).</item>
/// </list>
/// None of these methods call <c>SaveChanges</c> — the caller owns the unit of work.
/// </summary>
public static class ChoreRoomMembership
{
    /// <summary>
    /// choreId → sorted roomIds for the whole household. The board's single batched membership read
    /// (WP-04, WP-07) — one query joined in memory, NOT an N+1 per chore.
    /// </summary>
    public static async Task<Dictionary<int, IReadOnlyList<int>>> LoadMembershipsAsync(
        ApplicationDbContext context, int householdId, CancellationToken ct = default)
    {
        var rows = await context.ChoreRooms
            .Where(cr => cr.HouseholdId == householdId)
            .OrderBy(cr => cr.RoomId)
            .Select(cr => new { cr.ChoreId, cr.RoomId })
            .ToListAsync(ct);

        // OrderBy(RoomId) above is preserved within each group (stable LINQ-to-objects grouping),
        // so each chore's roomIds come out sorted ascending.
        return rows
            .GroupBy(r => r.ChoreId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<int>)g.Select(r => r.RoomId).ToList());
    }

    /// <summary>
    /// A single chore's sorted roomIds — the mutation-response projection (WP-04). One-chore query,
    /// NOT the board N+1 concern; used to reflect PERSISTED membership after a save.
    /// </summary>
    public static async Task<IReadOnlyList<int>> LoadMembershipsForChoreAsync(
        ApplicationDbContext context, int householdId, int choreId, CancellationToken ct = default)
    {
        return await context.ChoreRooms
            .Where(cr => cr.HouseholdId == householdId && cr.ChoreId == choreId)
            .OrderBy(cr => cr.RoomId)
            .Select(cr => cr.RoomId)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Add/remove ChoreRoom rows so the chore's memberships match <paramref name="desiredRoomIds"/>
    /// (distinct-normalized). Async because it must read the current rows to diff (WP-02).
    /// Does NOT validate that the rooms exist or update the shim — the caller (ChoreService) owns both.
    /// </summary>
    public static async Task ReconcileMembershipsAsync(
        ApplicationDbContext context, int householdId, int choreId,
        IReadOnlyCollection<int> desiredRoomIds, CancellationToken ct = default)
    {
        var desired = desiredRoomIds.Distinct().ToHashSet();

        var current = await context.ChoreRooms
            .Where(cr => cr.HouseholdId == householdId && cr.ChoreId == choreId)
            .ToListAsync(ct);

        var currentIds = current.Select(cr => cr.RoomId).ToHashSet();

        // Remove memberships no longer desired.
        foreach (var row in current.Where(cr => !desired.Contains(cr.RoomId)))
            context.ChoreRooms.Remove(row);

        // Add newly desired memberships.
        foreach (var roomId in desired.Where(id => !currentIds.Contains(id)))
            context.ChoreRooms.Add(new ChoreRoom
            {
                HouseholdId = householdId,
                ChoreId = choreId,
                RoomId = roomId
            });
    }
}
